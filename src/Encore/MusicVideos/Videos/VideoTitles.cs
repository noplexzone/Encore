using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Encore.MusicVideos.Videos
{
    /// <summary>
    /// Reads YouTube-style titles such as "Artist - Song (Official Music Video) [4K]":
    /// what kind of video it is, and the song title without the noise.
    /// </summary>
    public static class VideoTitles
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // Not a music video at all, whatever else the title says
        private static readonly Regex Excluded = new(
            @"\b(teaser|trailer|reaction|behind the scenes|making of|the making|interview|snippet|preview|tiktok|karaoke|8d audio|sped ?up|slowed|nightcore|fan ?made|unofficial|full album|album stream|playlist|commentary|tutorial|episode|documentary|announcement)\b|#shorts\b",
            Options);

        private static readonly Regex Audio = new(
            @"\b(official audio|audio only|visuali[sz]er|visual ?loop|static video|art ?track)\b|[\(\[]\s*audio\s*[\)\]]",
            Options);

        // Only in brackets or after a dash, so songs like "Live Forever" still count as official videos
        private static readonly Regex Lyric = new(
            @"\blyrics?\s+video\b|[\(\[][^\)\]]*\blyrics?\b[^\)\]]*[\)\]]",
            Options);

        private static readonly Regex Live = new(
            @"[\(\[\{][^\)\]\}]*\b(live|concert|sessions?|unplugged|performance)\b[^\)\]\}]*[\)\]\}]|\blive\s+(at|from|@)\s|\b(tiny desk|unplugged|in session)\b",
            Options);

        private static readonly Regex Official = new(
            @"\bofficial\s+([\w-]+\s+){0,3}?video\b|\bmusic\s+video\b|\bofficial\s+mv\b|[\(\[]\s*(m/?v|video|clip)\s*[\)\]]|\bvideo\s*clip\b|\bclip\s+officiel\b|\bofficial\s+(hd\s+)?clip\b",
            Options);

        // Bracketed parts dropped from the song title
        private static readonly Regex NoiseGroup = new(
            @"\s*[\(\[\{【]([^\)\]\}】]*\b(official|video|audio|lyrics?|visuali[sz]er|hd|hq|4k|8k|uhd|\d{3,4}p|remaster(ed)?|m/?v|clip|explicit|uncensored|dirty|clean version|premiere|out now|color(ed)?|restored)\b[^\)\]\}】]*)[\)\]\}】]",
            Options);

        private static readonly Regex TrailingNoise = new(
            @"\s*(\||//|•)\s.*$|\s+[-–—]\s+(official|music video|video|lyrics?|audio|hd|4k)\b.*$",
            Options);

        private static readonly Regex Featuring = new(@"\s*[\(\[]?\b(feat|ft|featuring)\b\.?\s.*?([\)\]]|$)", Options);

        private static readonly Regex Dash = new(@"\s+[-–—:]\s+|\s[-–—]|[-–—]\s", Options);

        public static VideoKind Classify(string title)
        {
            if (Excluded.IsMatch(title) || Audio.IsMatch(title))
            {
                return VideoKind.Other;
            }

            if (Lyric.IsMatch(title))
            {
                return VideoKind.Lyric;
            }

            var official = Official.IsMatch(title);

            if (Live.IsMatch(title))
            {
                return VideoKind.Live;
            }

            return official ? VideoKind.Official : VideoKind.Other;
        }

        /// <summary>
        /// "Artist - Song (Official Video) [HD]" becomes "Song". Featured artists are kept.
        /// </summary>
        public static string CleanTitle(string title, string artistName)
        {
            var result = title.Trim();
            result = StripArtistPrefix(result, artistName);
            result = TrailingNoise.Replace(result, string.Empty);

            string previous;
            do
            {
                previous = result;
                result = NoiseGroup.Replace(result, string.Empty);
            }
            while (result != previous);

            result = Regex.Replace(result, @"\s{2,}", " ").Trim().Trim('"', '\'', '“', '”', '-', '–', '—', ' ');
            return result.Length == 0 ? title.Trim() : result;
        }

        /// <summary>
        /// Lower case letters and digits only, without featured artists, for matching titles.
        /// </summary>
        public static string Normalize(string title)
        {
            var withoutFeat = Featuring.Replace(title, " ");
            var decomposed = withoutFeat.Replace("&", " and ").Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (var c in decomposed)
            {
                if (c is '\'' or '’' or '`')
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
                else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(' ');
                }
            }

            return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        }

        public static bool MentionsArtist(string text, string artistName)
        {
            var artist = Normalize(artistName);
            return artist.Length > 0 && $" {Normalize(text)} ".Contains($" {artist} ");
        }

        private static string StripArtistPrefix(string title, string artistName)
        {
            var match = Dash.Match(title);

            if (match.Success && match.Index > 0 && MentionsArtist(title[..match.Index], artistName))
            {
                return title[(match.Index + match.Length)..].Trim();
            }

            // "Song by Artist"
            var by = Regex.Match(title, @"^(.+?)\s+by\s+(.+)$", RegexOptions.IgnoreCase);
            if (by.Success && MentionsArtist(by.Groups[2].Value, artistName))
            {
                return by.Groups[1].Value.Trim();
            }

            return title;
        }
    }
}
