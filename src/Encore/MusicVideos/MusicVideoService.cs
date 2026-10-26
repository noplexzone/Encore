using Encore.Common;
using Encore.MusicVideos.Discovery;
using Encore.MusicVideos.Tools;
using Encore.MusicVideos.Videos;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Events;
using NzbDrone.Core.ProgressMessaging;
using NzbDrone.Core.ThingiProvider.Events;

namespace Encore.MusicVideos
{
    public class WorkerStatus
    {
        public string? Activity { get; set; }
        public int? ArtistId { get; set; }
        public string? VideoKey { get; set; }
        public string? Progress { get; set; }
        public int Queued { get; set; }
        public string? ToolProblem { get; set; }
        public string? LastError { get; set; }
    }

    /// <summary>
    /// What's queued or running for one artist, for the artist page.
    /// </summary>
    public class ArtistActivity
    {
        public bool Refreshing { get; set; }
        public bool SearchingAll { get; set; }
        public HashSet<string> QueuedKeys { get; } = [];
        public HashSet<string> Attempted { get; } = [];
        public string? DownloadingKey { get; set; }
        public string? Progress { get; set; }

        public bool IsQueued(MusicVideo video) =>
            !Attempted.Contains(video.Key) &&
            (QueuedKeys.Contains(video.Key) || (SearchingAll && video.Monitored && video.Status != VideoStatus.Downloaded));
    }

    public interface IMusicVideoService
    {
        MusicVideosSettings? Settings { get; }
        WorkerStatus Status { get; }
        ArtistActivity Activity(int artistId);
        void Refresh(Artist artist);
        void Search(Artist artist, string? videoKey);
        void AddUrl(Artist artist, string url);
        void SetMonitored(Artist artist, string? videoKey, bool monitored);
        void DeleteVideo(Artist artist, string videoKey);
    }

    /// <summary>
    /// Works like Lidarr does for albums. Refreshing an artist also looks for its music videos again,
    /// and searching an artist (or a section, or one video) downloads the monitored ones that are missing.
    /// Both run as Lidarr tasks, so they show at the bottom left like Lidarr's own.
    /// </summary>
    public class MusicVideoService : IMusicVideoService,
                                     IExecute<MusicVideoRefreshCommand>,
                                     IExecute<MusicVideoSearchCommand>,
                                     IExecute<MusicVideoAddCommand>,
                                     IHandle<ApplicationStartedEvent>,
                                     IHandle<ApplicationShutdownRequested>,
                                     IHandle<ArtistRefreshCompleteEvent>,
                                     IHandle<ArtistScannedEvent>,
                                     IHandle<CommandExecutedEvent>,
                                     IHandle<ProviderUpdatedEvent<IMetadata>>
    {
        public const string LinkName = "Videos";

        // Lidarr's scheduled refreshes look for new videos at most this often; a refresh by hand always does
        private static readonly TimeSpan AutomaticRefreshEvery = TimeSpan.FromDays(1);

        private readonly IMetadataFactory _metadataFactory;
        private readonly IArtistService _artistService;
        private readonly IVideoStore _store;
        private readonly IVideoFinder _finder;
        private readonly IVideoTools _tools;
        private readonly IArtistLinks _links;
        private readonly IManageCommandQueue _commandQueue;
        private readonly Logger _logger;
        private readonly string _tempRoot;

        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _lock = new();

        // What's running now; at most one refresh and one search, as the tasks are type exclusive
        private string? _refreshActivity;
        private int? _refreshArtistId;
        private int? _searchArtistId;
        private readonly HashSet<string> _attempted = [];
        private Download? _download;
        private string? _lastError;

        private sealed class Download
        {
            public int ArtistId { get; init; }
            public string Key { get; init; } = string.Empty;
            public string Activity { get; init; } = string.Empty;
            public bool AskedFor { get; init; }
            public CancellationTokenSource Cancel { get; init; } = null!;
            public string? Progress { get; set; }
        }

        private enum Outcome
        {
            Downloaded,
            Failed,
            Stopped
        }

        public MusicVideoService(IMetadataFactory metadataFactory,
                                 IArtistService artistService,
                                 IVideoStore store,
                                 IVideoFinder finder,
                                 IVideoTools tools,
                                 IArtistLinks links,
                                 IManageCommandQueue commandQueue,
                                 IAppFolderInfo appFolderInfo,
                                 Logger logger)
        {
            _metadataFactory = metadataFactory;
            _artistService = artistService;
            _store = store;
            _finder = finder;
            _tools = tools;
            _links = links;
            _commandQueue = commandQueue;
            _logger = logger;
            _tempRoot = Path.Combine(appFolderInfo.AppDataFolder, "Encore", "video-downloads");
        }

        public MusicVideosSettings? Settings =>
            _metadataFactory.Enabled().OfType<MusicVideosProvider>().FirstOrDefault()?.CurrentSettings;

        public WorkerStatus Status
        {
            get
            {
                lock (_lock)
                {
                    return new WorkerStatus
                    {
                        Activity = _download?.Activity ?? _refreshActivity,
                        ArtistId = _download?.ArtistId ?? _refreshArtistId,
                        VideoKey = _download?.Key,
                        Progress = _download?.Progress,
                        Queued = ActiveCommands().Count,
                        ToolProblem = _tools.LastProblem,
                        LastError = _lastError
                    };
                }
            }
        }

        public ArtistActivity Activity(int artistId)
        {
            var activity = new ArtistActivity();

            foreach (var command in ActiveCommands())
            {
                switch (command)
                {
                    case MusicVideoRefreshCommand refresh when refresh.ArtistIds.Contains(artistId):
                    case MusicVideoAddCommand add when add.ArtistId == artistId:
                        activity.Refreshing = true;
                        break;
                    case MusicVideoSearchCommand search when search.ArtistId == artistId:
                        if (search.VideoKeys == null)
                        {
                            activity.SearchingAll = true;
                        }
                        else
                        {
                            activity.QueuedKeys.UnionWith(search.VideoKeys);
                        }

                        break;
                }
            }

            lock (_lock)
            {
                if (_searchArtistId == artistId)
                {
                    activity.Attempted.UnionWith(_attempted);
                }

                if (_download?.ArtistId == artistId)
                {
                    activity.DownloadingKey = _download.Key;
                    activity.Progress = _download.Progress;
                }
            }

            return activity;
        }

        private List<Command> ActiveCommands() =>
            _commandQueue.All()
                .Where(c => c.Status is CommandStatus.Queued or CommandStatus.Started)
                .Select(c => c.Body)
                .Where(b => b is MusicVideoRefreshCommand or MusicVideoSearchCommand or MusicVideoAddCommand)
                .ToList();

        public void Refresh(Artist artist) =>
            _commandQueue.Push(new MusicVideoRefreshCommand { ArtistIds = [artist.Id] }, trigger: CommandTrigger.Manual);

        public void Search(Artist artist, string? videoKey)
        {
            if (videoKey != null)
            {
                Find(_store.Load(artist, RequireSettings()), videoKey);
            }

            _commandQueue.Push(new MusicVideoSearchCommand { ArtistId = artist.Id, VideoKeys = videoKey == null ? null : [videoKey] },
                               trigger: CommandTrigger.Manual);
        }

        public void AddUrl(Artist artist, string url) =>
            _commandQueue.Push(new MusicVideoAddCommand { ArtistId = artist.Id, Url = url }, trigger: CommandTrigger.Manual);

        /// <summary>
        /// Monitors or unmonitors one video, or with no key all of them and the ones found later.
        /// </summary>
        public void SetMonitored(Artist artist, string? videoKey, bool monitored)
        {
            var settings = RequireSettings();
            var changed = 0;

            _store.Update(artist, settings, state =>
            {
                var videos = videoKey == null ? state.Videos : [Find(state, videoKey)];

                foreach (var video in videos.Where(v => v.Monitored != monitored))
                {
                    video.Monitored = monitored;
                    changed++;
                }

                if (videoKey == null)
                {
                    // Only kept when it differs from the Monitor New Videos setting
                    state.MonitorNewVideos = monitored == settings.MonitorNewVideos ? null : monitored;
                }
            });

            _logger.Info(videoKey == null ? "Music videos: {0} all of {1}'s videos ({2} changed), and new ones will be {0}" : "Music videos: {0} {1} - {3}",
                         monitored ? "monitored" : "unmonitored",
                         artist.Name,
                         changed,
                         videoKey);

            if (!monitored)
            {
                StopDownload(artist.Id, videoKey);
            }
        }

        // Stops the download that's running if a search picked it because it was monitored
        private void StopDownload(int artistId, string? videoKey)
        {
            lock (_lock)
            {
                if (_download != null && !_download.AskedFor && _download.ArtistId == artistId &&
                    (videoKey == null || _download.Key == videoKey))
                {
                    _download.Cancel.Cancel();
                }
            }
        }

        private static MusicVideo Find(VideoState state, string videoKey) =>
            state.Videos.FirstOrDefault(v => v.Key == videoKey) ?? throw new KeyNotFoundException("Unknown video");

        public void DeleteVideo(Artist artist, string videoKey)
        {
            var settings = RequireSettings();
            _store.Update(artist, settings, state =>
            {
                var video = Find(state, videoKey);
                _store.DeleteFiles(artist, video, settings);
                video.File = null;
                video.Quality = null;
                video.DownloadedAt = null;

                // Deleting a video means it isn't wanted, or the next search would just get it again
                video.Status = VideoStatus.Wanted;
                video.Monitored = false;
            });
        }

        public void Handle(ApplicationStartedEvent message)
        {
            EnsureLinks();
            Task.Run(QueueFirstRefreshes);
        }

        public void Handle(ApplicationShutdownRequested message) => _shutdown.Cancel();

        // Refreshing an artist in Lidarr looks for its videos again
        public void Handle(ArtistRefreshCompleteEvent message)
        {
            var settings = Settings;
            EnsureLink(message.Artist, settings);

            if (settings == null || !Directory.Exists(message.Artist.Path))
            {
                return;
            }

            var refreshCommand = ProgressMessageContext.CommandModel;
            var byHand = refreshCommand?.Trigger == CommandTrigger.Manual;
            var state = _store.Load(message.Artist, settings);

            if (byHand || state.LastSearch == null || state.Incomplete || state.LastSearch < DateTime.UtcNow - AutomaticRefreshEvery)
            {
                _commandQueue.Push(new MusicVideoRefreshCommand { ArtistIds = [message.Artist.Id] },
                                   trigger: refreshCommand?.Trigger ?? CommandTrigger.Unspecified);
            }
        }

        // An artist added without a folder gets its videos once Lidarr has made one
        public void Handle(ArtistScannedEvent message)
        {
            var settings = Settings;

            if (settings != null && Directory.Exists(message.Artist.Path) && _store.Load(message.Artist, settings).LastSearch == null)
            {
                _commandQueue.Push(new MusicVideoRefreshCommand { ArtistIds = [message.Artist.Id] });
            }
        }

        // Searching an artist in Lidarr also searches its music videos
        public void Handle(CommandExecutedEvent message)
        {
            var artistId = message.Command.Body switch
            {
                ArtistSearchCommand search => search.ArtistId,
                MissingAlbumSearchCommand missing => missing.ArtistId,
                _ => null
            };

            if (artistId == null || Settings == null)
            {
                return;
            }

            var artist = FindArtist(artistId.Value);

            if (artist != null && Directory.Exists(artist.Path))
            {
                _commandQueue.Push(new MusicVideoSearchCommand { ArtistId = artist.Id }, trigger: message.Command.Trigger);
            }
        }

        public void Handle(ProviderUpdatedEvent<IMetadata> message)
        {
            if (message.Definition.Implementation == nameof(MusicVideosProvider))
            {
                EnsureLinks();
                Task.Run(QueueFirstRefreshes);
            }
        }

        // Artists never looked at, for example when Music Videos was just turned on
        private void QueueFirstRefreshes()
        {
            var settings = Settings;

            if (settings == null)
            {
                return;
            }

            try
            {
                var ids = _artistService.GetAllArtists()
                    .Where(a => Directory.Exists(a.Path))
                    .Where(a =>
                    {
                        var state = _store.Load(a, settings);
                        return state.LastSearch == null || state.Incomplete;
                    })
                    .Select(a => a.Id)
                    .ToList();

                if (ids.Count > 0)
                {
                    _commandQueue.Push(new MusicVideoRefreshCommand { ArtistIds = ids });
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Music videos: couldn't check which artists need a refresh");
            }
        }

        public void Execute(MusicVideoRefreshCommand message)
        {
            var settings = Settings;

            if (settings == null)
            {
                return;
            }

            foreach (var artistId in message.ArtistIds)
            {
                var artist = FindArtist(artistId);

                if (artist == null || !Directory.Exists(artist.Path))
                {
                    continue;
                }

                try
                {
                    Refresh(artist, settings);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && message.ArtistIds.Count > 1)
                {
                    _logger.Error(ex, "Music videos: couldn't refresh {0}", artist);
                }
                finally
                {
                    SetRefreshing(null, null);
                }
            }
        }

        private void Refresh(Artist artist, MusicVideosSettings settings)
        {
            _logger.ProgressInfo("Refreshing music videos for {0}", artist.Name);
            SetRefreshing($"Looking for videos by {artist.Name}", artist.Id);

            var removed = 0;
            var added = 0;

            // Today's rules first, so a better copy of a song can replace one that's dropped
            _store.Update(artist, settings, s =>
            {
                var before = s.Videos.Count;
                s.Videos = _finder.Tidy(artist, settings, s.Videos);
                removed = before - s.Videos.Count;
            });

            var tools = Remember(() => _tools.Resolve(settings, _shutdown.Token));
            var result = _finder.Find(artist, settings, _store.Load(artist, settings), tools, _shutdown.Token);

            _store.Update(artist, settings, s =>
            {
                var known = s.Videos.Select(v => v.Key).ToHashSet();
                var found = result.Videos.Where(v => known.Add(v.Key)).ToList();
                found.ForEach(v => v.Monitored = s.MonitorNewVideos ?? settings.MonitorNewVideos);
                s.Videos.AddRange(found);
                s.CheckedRecordings.UnionWith(result.CheckedRecordings);
                s.LastSearch = DateTime.UtcNow;
                s.Incomplete = result.Problems.Count > 0;
                added = found.Count;
            });

            _logger.ProgressInfo(removed > 0 ? "Found {0} for {1}, removed {2} that no longer fit" : "Found {0} for {1}",
                                 Videos(added, "new music video"), artist.Name, removed);
        }

        public void Execute(MusicVideoSearchCommand message)
        {
            var settings = Settings;
            var artist = FindArtist(message.ArtistId);

            if (settings == null || artist == null)
            {
                return;
            }

            if (!Directory.Exists(artist.Path))
            {
                throw new CommandFailedException("{0} has no folder yet", artist.Name);
            }

            var askedFor = message.VideoKeys != null;
            var keys = message.VideoKeys ?? _store.Load(artist, settings).Videos
                .Where(v => v.Monitored && v.Status != VideoStatus.Downloaded)
                .Select(v => v.Key)
                .ToList();

            if (keys.Count == 0)
            {
                _logger.ProgressInfo("No missing monitored music videos for {0}", artist.Name);
                return;
            }

            var tools = Remember(() => _tools.Resolve(settings, _shutdown.Token));
            var downloaded = 0;
            var failed = 0;
            var stopped = 0;

            lock (_lock)
            {
                _searchArtistId = artist.Id;
                _attempted.Clear();
            }

            try
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    var video = _store.Load(artist, settings).Videos.FirstOrDefault(v => v.Key == keys[i]);

                    // Unmonitored, or downloaded some other way, since the search started
                    if (video == null || (!askedFor && (!video.Monitored || video.Status == VideoStatus.Downloaded)))
                    {
                        continue;
                    }

                    _logger.ProgressInfo(keys.Count == 1 ? "Downloading music video {2} - {3}" : "Downloading music video {0} of {1}: {2} - {3}",
                                         i + 1, keys.Count, artist.Name, video.Title);

                    var outcome = DownloadVideo(artist, video, settings, tools, askedFor);
                    downloaded += outcome == Outcome.Downloaded ? 1 : 0;
                    failed += outcome == Outcome.Failed ? 1 : 0;
                    stopped += outcome == Outcome.Stopped ? 1 : 0;

                    lock (_lock)
                    {
                        _attempted.Add(video.Key);
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _searchArtistId = null;
                    _attempted.Clear();
                }
            }

            if (failed > 0 && downloaded == 0)
            {
                throw new CommandFailedException(failed == 1
                    ? $"Couldn't download a music video for {artist.Name}, see its Music Videos section"
                    : $"Couldn't download {failed} music videos for {artist.Name}, see its Music Videos section");
            }

            if (downloaded == 0 && failed == 0)
            {
                _logger.ProgressInfo(stopped > 0 ? "Stopped searching music videos for {0}, they were unmonitored" : "No missing monitored music videos for {0}", artist.Name);
                return;
            }

            _logger.ProgressInfo(failed > 0 ? "Downloaded {0} for {1}, {2} failed" : "Downloaded {0} for {1}",
                                 Videos(downloaded), artist.Name, failed);
        }

        public void Execute(MusicVideoAddCommand message)
        {
            var settings = Settings;
            var artist = FindArtist(message.ArtistId);

            if (settings == null || artist == null)
            {
                return;
            }

            var url = message.Url;
            _logger.ProgressInfo("Reading {0}", url);
            SetRefreshing($"Reading {url}", artist.Id);

            try
            {
                var tools = Remember(() => _tools.Resolve(settings, _shutdown.Token));

                if (YouTube.IsCollection(url))
                {
                    var videos = _finder.FromCollection(artist, settings, url, VideoFinder.SourceChannel, tools, _shutdown.Token);
                    var added = 0;

                    _store.Update(artist, settings, s =>
                    {
                        if (!s.ExtraSources.Contains(url, StringComparer.OrdinalIgnoreCase))
                        {
                            s.ExtraSources.Add(url);
                        }

                        var known = s.Videos.Select(v => v.Key).ToHashSet();
                        var found = videos.Where(v => known.Add(v.Key) && Allowed(v.Kind, settings)).ToList();
                        found.ForEach(v => v.Monitored = s.MonitorNewVideos ?? settings.MonitorNewVideos);
                        s.Videos.AddRange(found);
                        added = found.Count;
                    });

                    _logger.ProgressInfo("Added {0} for {1}", Videos(added), artist.Name);
                    return;
                }

                // A single video added by hand is wanted whatever its title says
                var listed = YtDlp.List(tools, settings, url, 1, _shutdown.Token).FirstOrDefault()
                             ?? throw new CommandFailedException("yt-dlp found no video at {0}", url);
                var video = VideoFinder.FromListing(artist, listed with { Url = url }, VideoFinder.SourceByHand);
                video.Key = YouTube.KeyForUrl(url);

                if (video.Kind == VideoKind.Other)
                {
                    video.Kind = VideoKind.Official;
                }

                _store.Update(artist, settings, s =>
                {
                    var existing = s.Videos.FirstOrDefault(v => v.Key == video.Key);

                    if (existing != null)
                    {
                        existing.Monitored = true;
                    }
                    else
                    {
                        s.Videos.Add(video);
                    }
                });

                SetRefreshing(null, null);
                video = _store.Load(artist, settings).Videos.First(v => v.Key == video.Key);
                _logger.ProgressInfo("Downloading music video {0} - {1}", artist.Name, video.Title);

                if (DownloadVideo(artist, video, settings, tools, true) == Outcome.Failed)
                {
                    throw new CommandFailedException("Couldn't download {0}, see the Videos page", url);
                }

                _logger.ProgressInfo("Downloaded music video {0} - {1}", artist.Name, video.Title);
            }
            finally
            {
                SetRefreshing(null, null);
            }
        }

        private Outcome DownloadVideo(Artist artist, MusicVideo video, MusicVideosSettings settings, ToolPaths tools, bool askedFor)
        {
            var temp = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

            lock (_lock)
            {
                _download = new Download
                {
                    ArtistId = artist.Id,
                    Key = video.Key,
                    Activity = $"Downloading {artist.Name} - {video.Title}",
                    AskedFor = askedFor,
                    Cancel = cancel
                };
            }

            try
            {
                var result = YtDlp.Download(tools, settings, video.Url, temp, SetProgress, cancel.Token);

                _store.Update(artist, settings, s =>
                {
                    var stored = Find(s, video.Key);
                    stored.Year ??= result.Year;
                    stored.Duration = result.Duration ?? stored.Duration;
                    stored.File = _store.Place(artist, stored, result.File, result.Thumbnail, settings);
                    stored.Quality = result.Quality;
                    stored.Status = VideoStatus.Downloaded;
                    stored.DownloadedAt = DateTime.UtcNow;
                    stored.Error = null;
                });

                _logger.Info("Music videos: downloaded {0} - {1} ({2})", artist.Name, video.Title, result.Quality);
                return Outcome.Downloaded;
            }
            catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
            {
                _logger.Info("Music videos: stopped downloading {0} - {1}, it was unmonitored", artist.Name, video.Title);
                return Outcome.Stopped;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn(ex, "Music videos: couldn't download {0} - {1} from {2}", artist.Name, video.Title, video.Url);
                _store.Update(artist, settings, s =>
                {
                    var stored = s.Videos.FirstOrDefault(v => v.Key == video.Key);

                    if (stored != null)
                    {
                        stored.Status = VideoStatus.Failed;
                        stored.Error = ex.Message;
                    }
                });

                lock (_lock)
                {
                    _lastError = ex.Message;
                }

                return Outcome.Failed;
            }
            finally
            {
                lock (_lock)
                {
                    _download = null;
                }

                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, true);
                }
            }
        }

        // Keeps a tool problem (no yt-dlp, no JavaScript runtime) for the Videos page as well as failing the task
        private T Remember<T>(Func<T> action)
        {
            try
            {
                var result = action();

                lock (_lock)
                {
                    _lastError = null;
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_lock)
                {
                    _lastError = ex.Message;
                }

                throw;
            }
        }

        private static string Videos(int count, string what = "music video") => count == 1 ? $"1 {what}" : $"{count} {what}s";

        private static bool Allowed(VideoKind kind, MusicVideosSettings settings) => kind switch
        {
            VideoKind.Official => true,
            VideoKind.Live => settings.IncludeLive,
            VideoKind.Lyric => settings.IncludeLyric,
            _ => false
        };

        private void SetRefreshing(string? activity, int? artistId)
        {
            lock (_lock)
            {
                _refreshActivity = activity;
                _refreshArtistId = artistId;
            }
        }

        private void SetProgress(string progress)
        {
            lock (_lock)
            {
                if (_download != null)
                {
                    _download.Progress = progress;
                }
            }
        }

        private void EnsureLinks()
        {
            var settings = Settings;

            if (settings == null)
            {
                return;
            }

            foreach (var artist in _artistService.GetAllArtists())
            {
                EnsureLink(artist, settings);
            }
        }

        private void EnsureLink(Artist artist, MusicVideosSettings? settings)
        {
            var baseUrl = _links.BaseUrl(settings?.LinkBaseUrl);

            if (settings == null || !settings.AddArtistLink || baseUrl == null)
            {
                return;
            }

            _links.Ensure(artist, LinkName, $"{baseUrl}/videos/{artist.Id}");
        }

        private Artist? FindArtist(int artistId)
        {
            try
            {
                return _artistService.GetArtist(artistId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NzbDrone.Core.Datastore.ModelNotFoundException)
            {
                return null;
            }
        }

        private MusicVideosSettings RequireSettings() =>
            Settings ?? throw new InvalidOperationException("Enable Music Videos under Settings > Metadata first");
    }
}
