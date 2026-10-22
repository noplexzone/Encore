using System.Text.Json;
using NLog;
using NzbDrone.Core.Music;

namespace Encore.MusicVideos.Videos
{
    public interface IVideoStore
    {
        string VideoFolder(Artist artist, MusicVideosSettings settings);
        VideoState Load(Artist artist, MusicVideosSettings settings);
        void Update(Artist artist, MusicVideosSettings settings, Action<VideoState> change);
        string? FilePath(Artist artist, MusicVideo video, MusicVideosSettings settings);
        string Place(Artist artist, MusicVideo video, string downloadedFile, string? thumbnail, MusicVideosSettings settings);
        void DeleteFiles(Artist artist, MusicVideo video, MusicVideosSettings settings);
    }

    public class VideoStore : IVideoStore
    {
        public const string StateFileName = "musicvideos.json";
        public const string ThumbSuffix = "-thumb";

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly object Lock = new();

        private readonly Logger _logger;

        public VideoStore(Logger logger)
        {
            _logger = logger;
        }

        public string VideoFolder(Artist artist, MusicVideosSettings settings) =>
            string.IsNullOrWhiteSpace(settings.VideoFolder) ? artist.Path : Path.Combine(artist.Path, settings.VideoFolder);

        public VideoState Load(Artist artist, MusicVideosSettings settings)
        {
            lock (Lock)
            {
                return Read(StatePath(artist, settings));
            }
        }

        public void Update(Artist artist, MusicVideosSettings settings, Action<VideoState> change)
        {
            lock (Lock)
            {
                var path = StatePath(artist, settings);
                var state = Read(path);
                change(state);

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temp, path, true);
            }
        }

        public string? FilePath(Artist artist, MusicVideo video, MusicVideosSettings settings) =>
            video.File == null ? null : Path.Combine(VideoFolder(artist, settings), video.File);

        /// <summary>
        /// Moves a finished download into the video folder under its final name, with its thumbnail and NFO.
        /// Returns the file name used.
        /// </summary>
        public string Place(Artist artist, MusicVideo video, string downloadedFile, string? thumbnail, MusicVideosSettings settings)
        {
            var folder = VideoFolder(artist, settings);
            Directory.CreateDirectory(folder);

            var extension = Path.GetExtension(downloadedFile).ToLowerInvariant();
            var baseName = VideoNaming.FileName(settings.FileNameFormat, artist.Name, video);
            var name = baseName;

            // Another video already uses this name (an official and a live video of the same song, say)
            for (var i = 2; IsTaken(folder, name + extension, video); i++)
            {
                name = $"{baseName} ({i})";
            }

            var target = Path.Combine(folder, name + extension);

            if (video.File != null && !video.File.Equals(name + extension, StringComparison.Ordinal))
            {
                DeleteFiles(artist, video, settings);
            }

            File.Move(downloadedFile, target, true);

            if (settings.SaveThumbnail && thumbnail != null && File.Exists(thumbnail))
            {
                File.Move(thumbnail, Path.Combine(folder, name + ThumbSuffix + ".jpg"), true);
            }

            if (settings.WriteNfo)
            {
                File.WriteAllText(Path.Combine(folder, name + ".nfo"), VideoNaming.Nfo(artist.Name, artist.ForeignArtistId, video));
            }

            _logger.Info("Saved music video {0}", target);
            return name + extension;
        }

        public void DeleteFiles(Artist artist, MusicVideo video, MusicVideosSettings settings)
        {
            var path = FilePath(artist, video, settings);

            if (path == null)
            {
                return;
            }

            var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));

            foreach (var file in new[] { path, stem + ".nfo", stem + ThumbSuffix + ".jpg" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }

        private bool IsTaken(string folder, string fileName, MusicVideo video) =>
            File.Exists(Path.Combine(folder, fileName)) && !fileName.Equals(video.File, StringComparison.Ordinal);

        private string StatePath(Artist artist, MusicVideosSettings settings) =>
            Path.Combine(VideoFolder(artist, settings), StateFileName);

        private VideoState Read(string path)
        {
            if (!File.Exists(path))
            {
                return new VideoState();
            }

            try
            {
                return JsonSerializer.Deserialize<VideoState>(File.ReadAllText(path)) ?? new VideoState();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to read {0}, starting fresh", path);
                return new VideoState();
            }
        }
    }
}
