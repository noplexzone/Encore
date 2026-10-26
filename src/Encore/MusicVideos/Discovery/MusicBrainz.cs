using System.Net;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Http;

namespace Encore.MusicVideos.Discovery
{
    public record MbRecording(string Id, string Title, string? Disambiguation, int? LengthSeconds);

    public interface IMusicBrainz
    {
        List<string> ArtistVideoChannels(string artistMbid);
        List<MbRecording> VideoRecordings(string artistMbid);
        List<string> RecordingUrls(string recordingId);
    }

    /// <summary>
    /// MusicBrainz marks recordings that are videos, and links many of them, and most artists,
    /// to YouTube. Requests are spaced over a second apart, as MusicBrainz asks.
    /// </summary>
    public class MusicBrainz : IMusicBrainz
    {
        private const string Api = "https://musicbrainz.org/ws/2";
        private const int PageSize = 100;
        private const int MaxRecordings = 500;
        private const int MaxAttempts = 4;

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public MusicBrainz(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public List<string> ArtistVideoChannels(string artistMbid)
        {
            using var json = Get($"{Api}/artist/{artistMbid}?inc=url-rels&fmt=json");
            return ParseVideoChannels(json.RootElement);
        }

        public List<MbRecording> VideoRecordings(string artistMbid)
        {
            var result = new List<MbRecording>();

            for (var offset = 0; offset < MaxRecordings; offset += PageSize)
            {
                var query = Uri.EscapeDataString($"arid:{artistMbid} AND video:true");
                using var json = Get($"{Api}/recording?query={query}&limit={PageSize}&offset={offset}&fmt=json");
                var page = ParseRecordings(json.RootElement);
                result.AddRange(page);

                var count = json.RootElement.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                if (page.Count < PageSize || offset + PageSize >= count)
                {
                    break;
                }
            }

            return result;
        }

        public List<string> RecordingUrls(string recordingId)
        {
            using var json = Get($"{Api}/recording/{recordingId}?inc=url-rels&fmt=json");
            return ParseUrls(json.RootElement);
        }

        /// <summary>
        /// The artist's YouTube channels. Channels linked as YouTube Music are left out, even when also
        /// linked on youtube.com: they're the auto-generated artist channels, with releases but no videos tab.
        /// </summary>
        public static List<string> ParseVideoChannels(JsonElement artist)
        {
            var links = ParseUrls(artist).Where(Videos.YouTube.IsCollection).ToList();
            var musicOnly = links.Where(IsYouTubeMusic).Select(ChannelId).OfType<string>().ToHashSet(StringComparer.Ordinal);

            return links
                .Where(l => !IsYouTubeMusic(l) && (ChannelId(l) is not { } id || !musicOnly.Contains(id)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsYouTubeMusic(string url) => url.Contains("music.youtube.com", StringComparison.OrdinalIgnoreCase);

        private static string? ChannelId(string url)
        {
            var match = System.Text.RegularExpressions.Regex.Match(url, @"/channel/(UC[A-Za-z0-9_-]{22})");
            return match.Success ? match.Groups[1].Value : null;
        }

        public static List<MbRecording> ParseRecordings(JsonElement root)
        {
            var result = new List<MbRecording>();

            if (!root.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var recording in recordings.EnumerateArray())
            {
                // Search matches on the query, but check the flag anyway
                if (recording.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.False)
                {
                    continue;
                }

                var id = recording.TryGetProperty("id", out var i) ? i.GetString() : null;
                var title = recording.TryGetProperty("title", out var t) ? t.GetString() : null;

                if (id == null || title == null)
                {
                    continue;
                }

                var disambiguation = recording.TryGetProperty("disambiguation", out var d) ? d.GetString() : null;
                int? length = recording.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() / 1000 : null;

                result.Add(new MbRecording(id, title, string.IsNullOrWhiteSpace(disambiguation) ? null : disambiguation, length));
            }

            return result;
        }

        public static List<string> ParseUrls(JsonElement entity)
        {
            var result = new List<string>();

            if (!entity.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var relation in relations.EnumerateArray())
            {
                if (relation.TryGetProperty("ended", out var ended) && ended.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (relation.TryGetProperty("url", out var url) && url.TryGetProperty("resource", out var resource) && resource.GetString() is { } link)
                {
                    result.Add(link);
                }
            }

            return result;
        }

        // MusicBrainz answers 503 when it's busy or when this address asks too often, so wait and try again
        private JsonDocument Get(string url)
        {
            var wait = TimeSpan.FromSeconds(3);

            for (var attempt = 1; ; attempt++)
            {
                var request = new HttpRequest(url, HttpAccept.Json)
                {
                    RateLimit = TimeSpan.FromSeconds(1.5),
                    RateLimitKey = "musicbrainz.org",
                    SuppressHttpErrorStatusCodes = [HttpStatusCode.ServiceUnavailable],
                    LogHttpError = false
                };

                _logger.Trace("MusicBrainz: {0}", url);
                var response = _httpClient.Get(request);

                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    return JsonDocument.Parse(response.Content);
                }

                if (attempt == MaxAttempts)
                {
                    throw new HttpException(response);
                }

                _logger.Debug("MusicBrainz is busy, trying again in {0}s: {1}", wait.TotalSeconds, url);
                Thread.Sleep(wait);
                wait *= 2;
            }
        }
    }
}
