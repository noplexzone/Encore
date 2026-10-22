namespace Encore.ArtistArt.Art
{
    /// <summary>
    /// Saved in the candidate folder as artwork.json. Records which candidate is chosen
    /// for each image type, and a stamp of what this plugin last wrote into Lidarr's
    /// cover cache so a fresh image from Lidarr can be told apart from our own copy.
    /// </summary>
    public class ArtworkState
    {
        public Dictionary<string, string> Selected { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CacheStamp> CacheStamps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public bool AdoptedExistingFiles { get; set; }
    }

    public record CacheStamp(long Length, long LastWriteTicks);
}
