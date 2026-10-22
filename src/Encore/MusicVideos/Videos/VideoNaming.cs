using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Encore.MusicVideos.Videos
{
    public static class VideoNaming
    {
        public const string TitleToken = "{Track Title}";

        private const int MaxNameLength = 180;

        public static string KindLabel(VideoKind kind) => kind switch
        {
            VideoKind.Official => "Official Video",
            VideoKind.Live => "Live",
            VideoKind.Lyric => "Lyric Video",
            _ => "Video"
        };

        /// <summary>
        /// File name without extension, from the File Name setting.
        /// </summary>
        public static string FileName(string format, string artistName, MusicVideo video)
        {
            var name = Regex.Replace(format, @"\{(Artist Name|Track Title|Video Type|Year)\}", m => m.Groups[1].Value.ToLowerInvariant() switch
            {
                "artist name" => artistName,
                "track title" => video.Title,
                "video type" => KindLabel(video.Kind),
                _ => video.Year?.ToString() ?? string.Empty
            }, RegexOptions.IgnoreCase);

            return CleanFileName(name);
        }

        public static string CleanFileName(string name)
        {
            var builder = new StringBuilder(name.Length);

            foreach (var c in name)
            {
                builder.Append(c switch
                {
                    ':' => " -",
                    '/' or '\\' or '|' => "-",
                    '"' => "'",
                    '?' or '*' or '<' or '>' => string.Empty,
                    _ when char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) => string.Empty,
                    _ => c.ToString()
                });
            }

            var result = Regex.Replace(builder.ToString(), @"\s{2,}", " ").Trim().TrimEnd('.', ' ');

            if (result.Length > MaxNameLength)
            {
                result = result[..MaxNameLength].TrimEnd('.', ' ');
            }

            return result.Length == 0 ? "Video" : result;
        }

        /// <summary>
        /// Kodi-style musicvideo NFO, which Jellyfin and Emby read too.
        /// </summary>
        public static string Nfo(string artistName, string? artistMbid, MusicVideo video)
        {
            var root = new XElement("musicvideo",
                new XElement("title", video.Title),
                new XElement("artist", artistName));

            if (video.Year != null)
            {
                root.Add(new XElement("year", video.Year));
            }

            if (video.Duration != null)
            {
                root.Add(new XElement("runtime", Math.Max(1, (int)Math.Round(video.Duration.Value / 60.0))));
            }

            root.Add(new XElement("genre", KindLabel(video.Kind)));

            var youTubeId = YouTube.IdFromKey(video.Key);
            if (youTubeId != null)
            {
                root.Add(new XElement("uniqueid", new XAttribute("type", "youtube"), new XAttribute("default", "true"), youTubeId));
            }

            if (artistMbid != null)
            {
                root.Add(new XElement("musicbrainzartistid", artistMbid));
            }

            if (video.RecordingId != null)
            {
                root.Add(new XElement("musicbrainztrackid", video.RecordingId));
            }

            root.Add(new XElement("plot", video.Url));

            return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Declaration + Environment.NewLine + root;
        }
    }
}
