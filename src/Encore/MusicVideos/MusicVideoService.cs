using System.Collections.Concurrent;
using Encore.Common;
using Encore.MusicVideos.Discovery;
using Encore.MusicVideos.Tools;
using Encore.MusicVideos.Videos;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Events;
using NzbDrone.Core.ThingiProvider.Events;

namespace Encore.MusicVideos
{
    public record VideoJob(int ArtistId, string? VideoKey, string? SourceUrl = null)
    {
        public bool IsSearch => VideoKey == null && SourceUrl == null;
    }

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

    public interface IMusicVideoService
    {
        MusicVideosSettings? Settings { get; }
        WorkerStatus Status { get; }
        bool IsQueued(int artistId, string? videoKey);
        void QueueSearch(int artistId);
        void QueueDownload(int artistId, string videoKey);
        void QueueAddUrl(int artistId, string url);
        void SetMonitored(Artist artist, string? videoKey, bool monitored);
        void DownloadNow(Artist artist, string videoKey);
        void DownloadMissing(Artist artist);
        void DeleteVideo(Artist artist, string videoKey);
    }

    /// <summary>
    /// Runs searches and downloads one at a time on its own thread, so long downloads never hold up
    /// Lidarr's own tasks. Each artist is searched again every few days, and right after it's added.
    /// </summary>
    public class MusicVideoService : IMusicVideoService,
                                     IHandle<ApplicationStartedEvent>,
                                     IHandle<ApplicationShutdownRequested>,
                                     IHandle<ArtistAddedEvent>,
                                     IHandle<ArtistRefreshCompleteEvent>,
                                     IHandle<ProviderUpdatedEvent<IMetadata>>
    {
        public const string LinkName = "Videos";

        private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(2);

        private readonly IMetadataFactory _metadataFactory;
        private readonly IArtistService _artistService;
        private readonly IVideoStore _store;
        private readonly IVideoFinder _finder;
        private readonly IVideoTools _tools;
        private readonly IArtistLinks _links;
        private readonly Logger _logger;
        private readonly string _tempRoot;

        private readonly BlockingCollection<VideoJob> _queue = new();
        private readonly ConcurrentDictionary<VideoJob, bool> _pending = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly WorkerStatus _status = new();
        private readonly object _startLock = new();
        private Thread? _worker;
        private Timer? _timer;

        public MusicVideoService(IMetadataFactory metadataFactory,
                                 IArtistService artistService,
                                 IVideoStore store,
                                 IVideoFinder finder,
                                 IVideoTools tools,
                                 IArtistLinks links,
                                 IAppFolderInfo appFolderInfo,
                                 Logger logger)
        {
            _metadataFactory = metadataFactory;
            _artistService = artistService;
            _store = store;
            _finder = finder;
            _tools = tools;
            _links = links;
            _logger = logger;
            _tempRoot = Path.Combine(appFolderInfo.AppDataFolder, "Encore", "video-downloads");
        }

        public MusicVideosSettings? Settings =>
            _metadataFactory.Enabled().OfType<MusicVideosProvider>().FirstOrDefault()?.CurrentSettings;

        public WorkerStatus Status
        {
            get
            {
                lock (_status)
                {
                    return new WorkerStatus
                    {
                        Activity = _status.Activity,
                        ArtistId = _status.ArtistId,
                        VideoKey = _status.VideoKey,
                        Progress = _status.Progress,
                        Queued = _pending.Count,
                        ToolProblem = _tools.LastProblem,
                        LastError = _status.LastError
                    };
                }
            }
        }

        public bool IsQueued(int artistId, string? videoKey) => _pending.ContainsKey(new VideoJob(artistId, videoKey));

        public void QueueSearch(int artistId) => Enqueue(new VideoJob(artistId, null));

        public void QueueDownload(int artistId, string videoKey) => Enqueue(new VideoJob(artistId, videoKey));

        public void QueueAddUrl(int artistId, string url) => Enqueue(new VideoJob(artistId, null, url));

        /// <summary>
        /// Monitors or unmonitors one video, or with no key all of them and the ones found later.
        /// </summary>
        public void SetMonitored(Artist artist, string? videoKey, bool monitored)
        {
            var settings = RequireSettings();
            _store.Update(artist, settings, state =>
            {
                if (videoKey == null)
                {
                    state.MonitorNew = monitored;
                    state.Videos.ForEach(v => v.Monitored = monitored);
                }
                else
                {
                    Find(state, videoKey).Monitored = monitored;
                }
            });

            if (monitored)
            {
                QueueWanted(artist, settings);
            }
        }

        /// <summary>
        /// Like an album's search button: monitors the video and downloads it now.
        /// </summary>
        public void DownloadNow(Artist artist, string videoKey)
        {
            var settings = RequireSettings();
            _store.Update(artist, settings, state =>
            {
                var video = Find(state, videoKey);
                video.Monitored = true;
                video.Error = null;

                if (video.Status == VideoStatus.Failed)
                {
                    video.Status = VideoStatus.Wanted;
                }
            });

            QueueDownload(artist.Id, videoKey);
        }

        /// <summary>
        /// Downloads every monitored video that's missing, failed ones included, whatever Auto Download says.
        /// </summary>
        public void DownloadMissing(Artist artist)
        {
            var settings = RequireSettings();
            var keys = new List<string>();

            _store.Update(artist, settings, state =>
            {
                foreach (var video in state.Videos.Where(v => v.Monitored && v.Status is VideoStatus.Wanted or VideoStatus.Failed))
                {
                    video.Status = VideoStatus.Wanted;
                    video.Error = null;
                    keys.Add(video.Key);
                }
            });

            keys.ForEach(key => QueueDownload(artist.Id, key));
        }

        private static MusicVideo Find(VideoState state, string videoKey) =>
            state.Videos.FirstOrDefault(v => v.Key == videoKey) ?? throw new KeyNotFoundException("Unknown video");

        public void DeleteVideo(Artist artist, string videoKey)
        {
            var settings = RequireSettings();
            _store.Update(artist, settings, state =>
            {
                var video = state.Videos.FirstOrDefault(v => v.Key == videoKey) ?? throw new KeyNotFoundException("Unknown video");
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
            lock (_startLock)
            {
                if (_worker != null)
                {
                    return;
                }

                _worker = new Thread(Work) { IsBackground = true, Name = "Encore music videos" };
                _worker.Start();
                _timer = new Timer(_ => QueueDueSearches(), null, FirstCheckAfter, CheckEvery);
            }

            EnsureLinks();
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _shutdown.Cancel();
            _timer?.Dispose();
            _queue.CompleteAdding();
        }

        public void Handle(ArtistAddedEvent message)
        {
            if (Settings != null)
            {
                QueueSearch(message.Artist.Id);
            }
        }

        public void Handle(ArtistRefreshCompleteEvent message) => EnsureLink(message.Artist, Settings);

        public void Handle(ProviderUpdatedEvent<IMetadata> message)
        {
            if (message.Definition.Implementation == nameof(MusicVideosProvider))
            {
                EnsureLinks();
                Task.Run(QueueDueSearches);
            }
        }

        private void Enqueue(VideoJob job)
        {
            if (!_queue.IsAddingCompleted && _pending.TryAdd(job, true))
            {
                _queue.Add(job);
            }
        }

        private void QueueDueSearches()
        {
            var settings = Settings;

            if (settings == null)
            {
                return;
            }

            try
            {
                var due = DateTime.UtcNow.AddDays(-settings.SearchIntervalDays);

                foreach (var artist in _artistService.GetAllArtists().Where(a => Directory.Exists(a.Path)))
                {
                    var state = _store.Load(artist, settings);

                    if (state.LastSearch == null || state.LastSearch < due)
                    {
                        QueueSearch(artist.Id);
                    }
                    else
                    {
                        // Retry wanted videos that weren't downloaded yet, for example after a restart
                        QueueWanted(artist, settings, state);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Music videos: couldn't check which artists need a search");
            }
        }

        private void Work()
        {
            try
            {
                foreach (var job in _queue.GetConsumingEnumerable(_shutdown.Token))
                {
                    try
                    {
                        Run(job);

                        lock (_status)
                        {
                            _status.LastError = null;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Music videos: {0} failed", job);

                        lock (_status)
                        {
                            _status.LastError = ex.Message;
                        }
                    }
                    finally
                    {
                        _pending.TryRemove(job, out _);
                        SetActivity(null, null, null);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void Run(VideoJob job)
        {
            var settings = Settings;
            var artist = FindArtist(job.ArtistId);

            if (settings == null || artist == null)
            {
                return;
            }

            if (!Directory.Exists(artist.Path))
            {
                _logger.Debug("Music videos: {0} has no folder yet", artist);
                return;
            }

            if (job.SourceUrl != null)
            {
                AddUrl(artist, job.SourceUrl, settings);
            }
            else if (job.IsSearch)
            {
                Search(artist, settings);
            }
            else
            {
                Download(artist, job.VideoKey!, settings);
            }
        }

        private void Search(Artist artist, MusicVideosSettings settings)
        {
            SetActivity($"Looking for videos by {artist.Name}", artist.Id, null);
            var tools = _tools.Resolve(settings, _shutdown.Token);
            var state = _store.Load(artist, settings);
            var result = _finder.Find(artist, settings, state, tools, _shutdown.Token);

            _store.Update(artist, settings, s =>
            {
                var known = s.Videos.Select(v => v.Key).ToHashSet();
                var added = result.Videos.Where(v => !known.Contains(v.Key)).ToList();
                added.ForEach(v => v.Monitored = s.MonitorNew);
                s.Videos.AddRange(added);
                s.CheckedRecordings.UnionWith(result.CheckedRecordings);

                // A search that failed outright is tried again a day later rather than after the full interval
                s.LastSearch = result.Problems.Count > 0 && result.Videos.Count == 0
                    ? DateTime.UtcNow.AddDays(1 - settings.SearchIntervalDays)
                    : DateTime.UtcNow;
            });

            _logger.Info("Music videos: found {0} new videos for {1}", result.Videos.Count, artist);
            QueueWanted(artist, settings);
        }

        private void AddUrl(Artist artist, string url, MusicVideosSettings settings)
        {
            SetActivity($"Reading {url}", artist.Id, null);
            var tools = _tools.Resolve(settings, _shutdown.Token);

            if (YouTube.IsCollection(url))
            {
                var videos = _finder.FromCollection(artist, settings, url, VideoFinder.SourceChannel, tools, _shutdown.Token);

                _store.Update(artist, settings, s =>
                {
                    if (!s.ExtraSources.Contains(url, StringComparer.OrdinalIgnoreCase))
                    {
                        s.ExtraSources.Add(url);
                    }

                    var known = s.Videos.Select(v => v.Key).ToHashSet();
                    var added = videos.Where(v => known.Add(v.Key) && Allowed(v.Kind, settings)).ToList();
                    added.ForEach(v => v.Monitored = s.MonitorNew);
                    s.Videos.AddRange(added);
                });

                QueueWanted(artist, settings);
                return;
            }

            // A single video added by hand is wanted whatever its title says
            var listed = YtDlp.List(tools, settings, url, 1, _shutdown.Token).FirstOrDefault()
                         ?? throw new InvalidOperationException($"yt-dlp found no video at {url}");
            var video = VideoFinder.FromListing(artist, listed with { Url = url }, "Added by hand");
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
                    existing.Status = existing.File != null ? VideoStatus.Downloaded : VideoStatus.Wanted;
                    existing.Monitored = true;
                }
                else
                {
                    s.Videos.Add(video);
                }
            });

            QueueDownload(artist.Id, video.Key);
        }

        private void Download(Artist artist, string videoKey, MusicVideosSettings settings)
        {
            var video = _store.Load(artist, settings).Videos.FirstOrDefault(v => v.Key == videoKey);

            // Unmonitored while it waited in the queue
            if (video == null || !video.Monitored)
            {
                return;
            }

            SetActivity($"Downloading {artist.Name} - {video.Title}", artist.Id, videoKey);
            var temp = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));

            try
            {
                var tools = _tools.Resolve(settings, _shutdown.Token);
                var result = YtDlp.Download(tools, settings, video.Url, temp, p => SetProgress(p), _shutdown.Token);

                _store.Update(artist, settings, s =>
                {
                    var stored = s.Videos.First(v => v.Key == videoKey);
                    stored.Year ??= result.Year;
                    stored.Duration = result.Duration ?? stored.Duration;
                    stored.File = _store.Place(artist, stored, result.File, result.Thumbnail, settings);
                    stored.Quality = result.Quality;
                    stored.Status = VideoStatus.Downloaded;
                    stored.DownloadedAt = DateTime.UtcNow;
                    stored.Error = null;
                });

                _logger.Info("Music videos: downloaded {0} - {1} ({2})", artist.Name, video.Title, result.Quality);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn(ex, "Music videos: couldn't download {0} - {1} from {2}", artist.Name, video.Title, video.Url);
                _store.Update(artist, settings, s =>
                {
                    var stored = s.Videos.First(v => v.Key == videoKey);
                    stored.Status = VideoStatus.Failed;
                    stored.Error = ex.Message;
                });
            }
            finally
            {
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, true);
                }
            }
        }

        // Automatic downloads follow Lidarr's monitoring, like albums: the artist and the video both monitored
        private void QueueWanted(Artist artist, MusicVideosSettings settings, VideoState? state = null)
        {
            if (!settings.AutoDownload || !artist.Monitored)
            {
                return;
            }

            state ??= _store.Load(artist, settings);

            foreach (var video in state.Videos.Where(v => v.Monitored && v.Status == VideoStatus.Wanted))
            {
                QueueDownload(artist.Id, video.Key);
            }
        }

        private static bool Allowed(VideoKind kind, MusicVideosSettings settings) => kind switch
        {
            VideoKind.Official => true,
            VideoKind.Live => settings.IncludeLive,
            VideoKind.Lyric => settings.IncludeLyric,
            _ => false
        };

        private void SetActivity(string? activity, int? artistId, string? videoKey)
        {
            lock (_status)
            {
                _status.Activity = activity;
                _status.ArtistId = artistId;
                _status.VideoKey = videoKey;
                _status.Progress = null;
            }
        }

        private void SetProgress(string progress)
        {
            lock (_status)
            {
                _status.Progress = progress;
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
