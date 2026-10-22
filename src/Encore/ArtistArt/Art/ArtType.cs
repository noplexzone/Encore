using NzbDrone.Core.MediaCover;

namespace Encore.ArtistArt.Art
{
    /// <summary>
    /// One kind of artist image (poster, fanart, ...): how candidates are named,
    /// which Lidarr cover it replaces, and which files it writes into the artist folder.
    /// </summary>
    public sealed record ArtType(
        string Key,
        string Label,
        MediaCoverTypes CoverType,
        string[] Prefixes,
        int[] CacheHeights,
        Func<ArtistArtSettings, string[]> FolderFileNames)
    {
        public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

        public static readonly ArtType Poster = new(
            "poster",
            "Artist image",
            MediaCoverTypes.Poster,
            ["poster", "artist", "folder"],
            [500, 250],
            s => Names((s.WriteArtistFile, "artist"), (s.WriteFolderFile, "folder")));

        public static readonly ArtType Fanart = new(
            "fanart",
            "Background",
            MediaCoverTypes.Fanart,
            ["fanart", "backdrop", "background"],
            [360, 180],
            s => Names((s.WriteMediaServerFiles, "fanart")));

        public static readonly ArtType Banner = new(
            "banner",
            "Banner",
            MediaCoverTypes.Banner,
            ["banner"],
            [70, 35],
            s => Names((s.WriteMediaServerFiles, "banner")));

        public static readonly ArtType Logo = new(
            "logo",
            "Logo",
            MediaCoverTypes.Logo,
            ["logo", "clearlogo"],
            [500, 250],
            s => Names((s.WriteMediaServerFiles, "logo")));

        public static readonly IReadOnlyList<ArtType> All = [Poster, Fanart, Banner, Logo];

        public static ArtType? Find(string? key) =>
            All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>Matches candidate files such as "poster.jpg", "poster-2.png" or "fanart_live.webp".</summary>
        public bool IsCandidate(string fileName)
        {
            var extension = Path.GetExtension(fileName);
            if (!ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            return Prefixes.Any(p => stem.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                     (stem.StartsWith(p, StringComparison.OrdinalIgnoreCase) &&
                                      stem.Length > p.Length && "-_. ".Contains(stem[p.Length])));
        }

        private static string[] Names(params (bool Enabled, string Name)[] names) =>
            names.Where(n => n.Enabled).Select(n => n.Name).ToArray();
    }
}
