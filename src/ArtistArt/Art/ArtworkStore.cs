using System.Text.Json;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;

namespace ArtistArt.Art
{
    public record Candidate(string File, long Size, DateTime Modified, bool FromLidarr);

    public interface IArtworkStore
    {
        string CandidateFolder(Artist artist, ArtistArtSettings settings);
        ArtworkState Load(Artist artist, ArtistArtSettings settings);
        List<Candidate> Candidates(Artist artist, ArtType type, ArtistArtSettings settings);
        string? CandidatePath(Artist artist, ArtType type, string file, ArtistArtSettings settings);
        void EnsureInitialized(Artist artist, ArtistArtSettings settings);
        void CaptureLidarrImages(Artist artist, ArtistArtSettings settings);
        void Select(Artist artist, ArtType type, string file, ArtistArtSettings settings);
        void Reset(Artist artist, ArtType type, ArtistArtSettings settings);
        string AddCandidate(Artist artist, ArtType type, string originalName, Stream content, ArtistArtSettings settings);
        void DeleteCandidate(Artist artist, ArtType type, string file, ArtistArtSettings settings);
        bool ApplyToCache(Artist artist, ArtistArtSettings settings);
    }

    public class ArtworkStore : IArtworkStore
    {
        private const string StateFileName = "artwork.json";
        private const string LidarrSuffix = "-lidarr";

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly object Lock = new();

        private readonly IImageResizer _resizer;
        private readonly string _coverRoot;
        private readonly Logger _logger;

        public ArtworkStore(IImageResizer resizer, IAppFolderInfo appFolderInfo, Logger logger)
        {
            _resizer = resizer;
            _coverRoot = appFolderInfo.GetMediaCoverPath();
            _logger = logger;
        }

        public string CandidateFolder(Artist artist, ArtistArtSettings settings) =>
            Path.Combine(artist.Path, settings.CandidateFolder);

        public ArtworkState Load(Artist artist, ArtistArtSettings settings)
        {
            var path = Path.Combine(CandidateFolder(artist, settings), StateFileName);

            if (!File.Exists(path))
            {
                return new ArtworkState();
            }

            try
            {
                var state = JsonSerializer.Deserialize<ArtworkState>(File.ReadAllText(path)) ?? new ArtworkState();
                state.Selected = new Dictionary<string, string>(state.Selected, StringComparer.OrdinalIgnoreCase);
                state.CacheStamps = new Dictionary<string, CacheStamp>(state.CacheStamps, StringComparer.OrdinalIgnoreCase);
                return state;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to read {0}, starting fresh", path);
                return new ArtworkState();
            }
        }

        public List<Candidate> Candidates(Artist artist, ArtType type, ArtistArtSettings settings)
        {
            var folder = CandidateFolder(artist, settings);

            if (!Directory.Exists(folder))
            {
                return [];
            }

            return new DirectoryInfo(folder).GetFiles()
                .Where(f => type.IsCandidate(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new Candidate(f.Name, f.Length, f.LastWriteTimeUtc, IsLidarrCopy(type, f.Name)))
                .ToList();
        }

        public string? CandidatePath(Artist artist, ArtType type, string file, ArtistArtSettings settings)
        {
            // Only bare file names that are real candidates of this type, never a path
            if (file.IsNullOrWhiteSpace() || Path.GetFileName(file) != file || !type.IsCandidate(file))
            {
                return null;
            }

            var path = Path.Combine(CandidateFolder(artist, settings), file);
            return File.Exists(path) ? path : null;
        }

        public void EnsureInitialized(Artist artist, ArtistArtSettings settings)
        {
            lock (Lock)
            {
                var state = Load(artist, settings);

                if (!state.AdoptedExistingFiles)
                {
                    AdoptExistingFolderImages(artist, settings);
                    state.AdoptedExistingFiles = true;
                    Save(artist, state, settings);
                }
            }

            CaptureLidarrImages(artist, settings);
        }

        public void CaptureLidarrImages(Artist artist, ArtistArtSettings settings)
        {
            if (!settings.SaveLidarrImages)
            {
                return;
            }

            lock (Lock)
            {
                var state = Load(artist, settings);

                foreach (var type in ArtType.All)
                {
                    var cachePath = CachePath(artist, type, null);

                    if (!File.Exists(cachePath))
                    {
                        continue;
                    }

                    // If the cache still holds the copy we wrote, it isn't Lidarr's image
                    if (state.CacheStamps.TryGetValue(type.Key, out var stamp) && Matches(cachePath, stamp))
                    {
                        continue;
                    }

                    var destination = Path.Combine(CandidateFolder(artist, settings), type.Key + LidarrSuffix + Path.GetExtension(cachePath));

                    if (!SameContent(cachePath, destination))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(cachePath, destination, true);
                        _logger.Debug("Saved Lidarr's {0} for {1} as {2}", type.Key, artist, destination);
                    }
                }
            }
        }

        public void Select(Artist artist, ArtType type, string file, ArtistArtSettings settings)
        {
            var source = CandidatePath(artist, type, file, settings) ??
                         throw new FileNotFoundException($"No {type.Key} candidate named {file}");

            lock (Lock)
            {
                var state = Load(artist, settings);

                WriteFolderFiles(artist, type, source, settings);
                state.Selected[type.Key] = file;
                state.CacheStamps.Remove(type.Key);
                Save(artist, state, settings);
            }

            _logger.Info("{0} for {1} set to {2}", type.Label, artist, file);
        }

        public void Reset(Artist artist, ArtType type, ArtistArtSettings settings)
        {
            lock (Lock)
            {
                var state = Load(artist, settings);
                state.Selected.Remove(type.Key);
                state.CacheStamps.Remove(type.Key);
                Save(artist, state, settings);
            }

            // Put Lidarr's own image back where we can
            var lidarrCopy = Candidates(artist, type, settings).FirstOrDefault(c => c.FromLidarr);
            if (lidarrCopy != null)
            {
                var source = Path.Combine(CandidateFolder(artist, settings), lidarrCopy.File);
                WriteFolderFiles(artist, type, source, settings);
                WriteCache(artist, type, source);
            }
        }

        public string AddCandidate(Artist artist, ArtType type, string originalName, Stream content, ArtistArtSettings settings)
        {
            var extension = Path.GetExtension(originalName).ToLowerInvariant();

            if (!ArtType.ImageExtensions.Contains(extension))
            {
                throw new InvalidOperationException("Only .jpg, .jpeg, .png and .webp images are supported");
            }

            var folder = CandidateFolder(artist, settings);
            Directory.CreateDirectory(folder);

            // Numbers are unique across extensions so poster-1.png and poster-1.jpg never coexist
            var number = 0;
            do
            {
                number++;
            }
            while (ArtType.ImageExtensions.Any(e => File.Exists(Path.Combine(folder, $"{type.Key}-{number}{e}"))));

            var name = $"{type.Key}-{number}{extension}";

            using (var output = File.Create(Path.Combine(folder, name)))
            {
                content.CopyTo(output);
            }

            return name;
        }

        public void DeleteCandidate(Artist artist, ArtType type, string file, ArtistArtSettings settings)
        {
            var path = CandidatePath(artist, type, file, settings) ??
                       throw new FileNotFoundException($"No {type.Key} candidate named {file}");

            lock (Lock)
            {
                var state = Load(artist, settings);

                if (string.Equals(state.Selected.GetValueOrDefault(type.Key), file, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Choose another image before deleting the active one");
                }

                File.Delete(path);
            }
        }

        /// <summary>
        /// Copies every chosen image over Lidarr's cached cover so the Lidarr UI shows it.
        /// Returns true when anything was written.
        /// </summary>
        public bool ApplyToCache(Artist artist, ArtistArtSettings settings)
        {
            var written = false;

            lock (Lock)
            {
                var state = Load(artist, settings);

                foreach (var (key, file) in state.Selected.ToList())
                {
                    var type = ArtType.Find(key);
                    var source = type == null ? null : CandidatePath(artist, type, file, settings);

                    if (type == null || source == null)
                    {
                        _logger.Warn("Chosen {0} image {1} for {2} is missing, leaving Lidarr's image", key, file, artist);
                        continue;
                    }

                    var cachePath = CachePath(artist, type, null);

                    if (state.CacheStamps.TryGetValue(type.Key, out var stamp) && Matches(cachePath, stamp))
                    {
                        continue;
                    }

                    WriteCache(artist, type, source);
                    var info = new FileInfo(cachePath);
                    state.CacheStamps[type.Key] = new CacheStamp(info.Length, info.LastWriteTimeUtc.Ticks);
                    written = true;
                }

                if (written)
                {
                    Save(artist, state, settings);
                }
            }

            return written;
        }

        private void AdoptExistingFolderImages(Artist artist, ArtistArtSettings settings)
        {
            // Before we ever overwrite artist.jpg, folder.jpg and the like, keep a copy as a candidate
            foreach (var type in ArtType.All)
            {
                foreach (var baseName in type.FolderFileNames(AllFolderFiles))
                {
                    foreach (var existing in ArtType.ImageExtensions.Select(e => Path.Combine(artist.Path, baseName + e)).Where(File.Exists))
                    {
                        var destination = Path.Combine(CandidateFolder(artist, settings), $"{type.Key}-existing-{baseName}{Path.GetExtension(existing)}");

                        if (!File.Exists(destination))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            File.Copy(existing, destination);
                            _logger.Debug("Kept existing {0} as candidate {1}", existing, destination);
                        }
                    }
                }
            }
        }

        private static readonly ArtistArtSettings AllFolderFiles = new()
        {
            WriteArtistFile = true,
            WriteFolderFile = true,
            WriteMediaServerFiles = true
        };

        private void WriteFolderFiles(Artist artist, ArtType type, string source, ArtistArtSettings settings)
        {
            var extension = Path.GetExtension(source).ToLowerInvariant();

            foreach (var baseName in type.FolderFileNames(settings))
            {
                // Remove other formats so a stale artist.png can't win over a new artist.jpg
                foreach (var stale in ArtType.ImageExtensions.Where(e => e != extension).Select(e => Path.Combine(artist.Path, baseName + e)).Where(File.Exists))
                {
                    File.Delete(stale);
                }

                File.Copy(source, Path.Combine(artist.Path, baseName + extension), true);
            }
        }

        private void WriteCache(Artist artist, ArtType type, string source)
        {
            var cachePath = CachePath(artist, type, null);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.Copy(source, cachePath, true);
            File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);

            foreach (var height in type.CacheHeights)
            {
                var resized = CachePath(artist, type, height);

                try
                {
                    _resizer.Resize(cachePath, resized, height);
                }
                catch (Exception ex)
                {
                    // Lidarr falls back to the full size image when a resized one is missing
                    _logger.Debug(ex, "Couldn't resize {0} to {1}px", cachePath, height);
                    if (File.Exists(resized))
                    {
                        File.Delete(resized);
                    }
                }
            }
        }

        private string CachePath(Artist artist, ArtType type, int? height)
        {
            // Use the extension Lidarr's UI asks for, whatever format the chosen file is in
            var extension = type.CoverType == MediaCoverTypes.Clearlogo
                ? ".png"
                : artist.Metadata?.Value?.Images?.FirstOrDefault(i => i.CoverType == type.CoverType)?.Extension ?? ".jpg";

            var suffix = height.HasValue ? "-" + height : string.Empty;
            return Path.Combine(_coverRoot, artist.Id.ToString(), type.CoverType.ToString().ToLowerInvariant() + suffix + extension);
        }

        private void Save(Artist artist, ArtworkState state, ArtistArtSettings settings)
        {
            var folder = CandidateFolder(artist, settings);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, StateFileName), JsonSerializer.Serialize(state, JsonOptions));
        }

        private static bool IsLidarrCopy(ArtType type, string file) =>
            Path.GetFileNameWithoutExtension(file).Equals(type.Key + LidarrSuffix, StringComparison.OrdinalIgnoreCase);

        private static bool Matches(string path, CacheStamp stamp)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == stamp.Length && info.LastWriteTimeUtc.Ticks == stamp.LastWriteTicks;
        }

        private static bool SameContent(string a, string b)
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);

            return infoA.Exists && infoB.Exists && infoA.Length == infoB.Length &&
                   File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
