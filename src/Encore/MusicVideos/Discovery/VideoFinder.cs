using System.Text.RegularExpressions;
using Encore.MusicVideos.Tools;
using Encore.MusicVideos.Videos;
using NLog;
using NzbDrone.Core.Music;

namespace Encore.MusicVideos.Discovery
{
    public record FindResult(List<MusicVideo> Videos, List<string> CheckedRecordings, List<string> Problems);

    public interface IVideoFinder
    {
        FindResult Find(Artist artist, MusicVideosSettings settings, VideoState state, ToolPaths tools, CancellationToken cancellationToken);
        List<MusicVideo> FromCollection(Artist artist, MusicVideosSettings settings, string url, string source, ToolPaths tools, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Finds an artist's music videos in three places, best first: video recordings on MusicBrainz
    /// with a YouTube link, a YouTube search for MusicBrainz videos without one, and the uploads of
    /// the artist's YouTube channels (their own and VEVO) as linked on MusicBrainz.
    /// </summary>
    public class VideoFinder : IVideoFinder
    {
        public const string SourceMusicBrainz = "MusicBrainz";
        public const string SourceSearch = "YouTube search";
        public const string SourceChannel = "YouTube channel";

        // Each MusicBrainz lookup costs a second, so a large catalogue is worked through over several searches
        private const int MaxRecordingLookups = 60;
        private const int MaxChannelVideos = 1500;
        private const int MinSeconds = 60;
        private const int MaxSeconds = 20 * 60;

        private static readonly Regex OtherVideoSite = new(@"^https?://(www\.)?(vimeo\.com/\d+|dailymotion\.com/video/)", RegexOptions.IgnoreCase);

        private readonly IMusicBrainz _musicBrainz;
        private readonly ITrackService _trackService;
        private readonly Logger _logger;

        public VideoFinder(IMusicBrainz musicBrainz, ITrackService trackService, Logger logger)
        {
            _musicBrainz = musicBrainz;
            _trackService = trackService;
            _logger = logger;
        }

        public FindResult Find(Artist artist, MusicVideosSettings settings, VideoState state, ToolPaths tools, CancellationToken cancellationToken)
        {
            var found = new List<MusicVideo>();
            var checkedRecordings = new List<string>();
            var problems = new List<string>();
            var mbid = artist.ForeignArtistId;

            void Attempt(string what, Action action)
            {
                try
                {
                    action();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warn(ex, "Music videos: {0} failed for {1}", what, artist);
                    problems.Add($"{what}: {ex.Message}");
                }
            }

            Attempt("MusicBrainz videos", () =>
            {
                var recordings = _musicBrainz.VideoRecordings(mbid)
                    .Where(r => !state.CheckedRecordings.Contains(r.Id))
                    .Take(MaxRecordingLookups);

                foreach (var recording in recordings)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var video = FromRecording(artist, settings, recording, tools, cancellationToken);
                    if (video != null)
                    {
                        found.Add(video);
                    }

                    checkedRecordings.Add(recording.Id);
                }
            });

            var channels = new List<string>();
            Attempt("MusicBrainz artist links", () => channels.AddRange(_musicBrainz.ArtistVideoChannels(mbid)));
            channels.AddRange(state.ExtraSources);

            foreach (var channel in channels.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Attempt($"Channel {channel}", () => found.AddRange(FromCollection(artist, settings, channel, SourceChannel, tools, cancellationToken)));
            }

            return new FindResult(Filter(artist, settings, state, found), checkedRecordings, problems);
        }

        public List<MusicVideo> FromCollection(Artist artist, MusicVideosSettings settings, string url, string source, ToolPaths tools, CancellationToken cancellationToken)
        {
            return YtDlp.List(tools, settings, YouTube.VideosTab(url), MaxChannelVideos, cancellationToken)
                .Where(e => e.Duration is null or (>= MinSeconds and <= MaxSeconds))
                .Select(e => FromListing(artist, e, source))
                .Where(v => v.Kind != VideoKind.Other)
                .ToList();
        }

        public static MusicVideo FromListing(Artist artist, ListedVideo entry, string source)
        {
            var id = YouTube.IdFromUrl(entry.Url) ?? (Regex.IsMatch(entry.Id, "^[A-Za-z0-9_-]{11}$") ? entry.Id : null);

            return new MusicVideo
            {
                Key = id != null ? YouTube.Key(id) : entry.Url,
                Url = id != null ? YouTube.WatchUrl(id) : entry.Url,
                SourceTitle = entry.Title,
                Title = VideoTitles.CleanTitle(entry.Title, artist.Name),
                Kind = VideoTitles.Classify(entry.Title),
                Source = source,
                Thumbnail = id != null ? YouTube.Thumbnail(id) : null,
                Duration = entry.Duration,
                Added = DateTime.UtcNow
            };
        }

        private MusicVideo? FromRecording(Artist artist, MusicVideosSettings settings, MbRecording recording, ToolPaths tools, CancellationToken cancellationToken)
        {
            var label = recording.Disambiguation == null ? recording.Title : $"{recording.Title} ({recording.Disambiguation})";
            var kind = VideoTitles.Classify($"{recording.Title} ({recording.Disambiguation ?? "official video"})");

            if (kind == VideoKind.Other)
            {
                // MusicBrainz only flags real videos, so anything not live or lyric counts as the official one
                kind = VideoKind.Official;
            }

            var urls = _musicBrainz.RecordingUrls(recording.Id);
            var link = urls.FirstOrDefault(u => YouTube.IdFromUrl(u) != null) ?? urls.FirstOrDefault(u => OtherVideoSite.IsMatch(u));
            string source = SourceMusicBrainz;

            if (link == null)
            {
                link = SearchYouTube(artist, settings, recording.Title, kind, tools, cancellationToken);
                source = SourceSearch;
            }

            if (link == null)
            {
                _logger.Debug("No video found for MusicBrainz recording {0} by {1}", label, artist);
                return null;
            }

            var id = YouTube.IdFromUrl(link);

            return new MusicVideo
            {
                Key = YouTube.KeyForUrl(link),
                Url = id != null ? YouTube.WatchUrl(id) : link,
                SourceTitle = label,
                Title = VideoTitles.CleanTitle(recording.Title, artist.Name),
                Kind = kind,
                Source = source,
                Thumbnail = id != null ? YouTube.Thumbnail(id) : null,
                Duration = recording.LengthSeconds,
                RecordingId = recording.Id,
                Added = DateTime.UtcNow
            };
        }

        private string? SearchYouTube(Artist artist, MusicVideosSettings settings, string title, VideoKind kind, ToolPaths tools, CancellationToken cancellationToken)
        {
            var query = $"ytsearch8:{artist.Name} {title} {(kind == VideoKind.Live ? "live" : "official video")}";
            var wanted = VideoTitles.Normalize(title);

            var match = YtDlp.List(tools, settings, query, 8, cancellationToken).FirstOrDefault(e =>
                VideoTitles.Classify(e.Title) == kind &&
                VideoTitles.Normalize(VideoTitles.CleanTitle(e.Title, artist.Name)) == wanted &&
                (VideoTitles.MentionsArtist(e.Title, artist.Name) || (e.Channel != null && VideoTitles.MentionsArtist(e.Channel, artist.Name))));

            return match?.Url;
        }

        private List<MusicVideo> Filter(Artist artist, MusicVideosSettings settings, VideoState state, List<MusicVideo> found)
        {
            HashSet<string>? library = null;

            if (settings.OnlyLibraryTracks)
            {
                library = _trackService.GetTracksByArtist(artist.Id).Select(t => VideoTitles.Normalize(t.Title)).ToHashSet();
            }

            // Already known videos, ignored ones included, so the same song from another channel isn't offered again
            var keys = state.Videos.Select(v => v.Key).ToHashSet();
            var songs = state.Videos.Select(SongKey).ToHashSet();
            var result = new List<MusicVideo>();

            // Stable, so sources keep their order between copies that promise the same quality
            foreach (var video in found.OrderByDescending(v => VideoTitles.QualityHint(v.SourceTitle)))
            {
                var allowed = video.Kind switch
                {
                    VideoKind.Official => true,
                    VideoKind.Live => settings.IncludeLive,
                    VideoKind.Lyric => settings.IncludeLyric,
                    _ => false
                };

                if (!allowed || (library != null && !library.Contains(VideoTitles.Normalize(video.Title))))
                {
                    continue;
                }

                if (keys.Add(video.Key) && songs.Add(SongKey(video)))
                {
                    result.Add(video);
                }
            }

            return result;
        }

        // Live videos are often several per song, so they're told apart by their full title
        private static string SongKey(MusicVideo video) =>
            video.Kind == VideoKind.Live
                ? $"{video.Kind}:{VideoTitles.Normalize(video.SourceTitle)}"
                : $"{video.Kind}:{VideoTitles.Normalize(video.Title)}";
    }
}
