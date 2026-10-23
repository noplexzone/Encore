using System.Text.RegularExpressions;

namespace Encore.MusicVideos.Videos
{
    public static class YouTube
    {
        private const string KeyPrefix = "youtube:";

        private static readonly Regex VideoId = new(
            @"^https?://(?:(?:www|m|music)\.)?(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|v/|shorts/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})(?:[?&#/]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Collection = new(
            @"^https?://(?:(?:www|m|music)\.)?youtube\.com/(?:@[^/?#]+|channel/[^/?#]+|c/[^/?#]+|user/[^/?#]+|playlist\?(?:.*&)?list=)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string? IdFromUrl(string url)
        {
            var match = VideoId.Match(url.Trim());
            return match.Success ? match.Groups[1].Value : null;
        }

        public static string Key(string id) => KeyPrefix + id;

        public static string? IdFromKey(string key) =>
            key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key[KeyPrefix.Length..] : null;

        public static string WatchUrl(string id) => $"https://www.youtube.com/watch?v={id}";

        public static string Thumbnail(string id) => $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";

        /// <summary>
        /// A channel or playlist page rather than a single video.
        /// </summary>
        public static bool IsCollection(string url) => Collection.IsMatch(url.Trim()) && IdFromUrl(url) == null;

        /// <summary>
        /// The uploads list of a channel, which yt-dlp reads as a playlist.
        /// </summary>
        public static string VideosTab(string channelUrl)
        {
            var url = channelUrl.Trim().TrimEnd('/');

            if (url.Contains("list=", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(url, @"/(videos|streams|shorts|playlists|featured)$", RegexOptions.IgnoreCase))
            {
                return url;
            }

            return url + "/videos";
        }

        /// <summary>
        /// Key for any video address: YouTube ids are normalized, anything else is kept as is.
        /// </summary>
        public static string KeyForUrl(string url)
        {
            var id = IdFromUrl(url);
            return id != null ? Key(id) : url.Trim();
        }
    }
}
