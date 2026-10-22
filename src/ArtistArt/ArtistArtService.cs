using ArtistArt.Art;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Events;
using NzbDrone.Core.ThingiProvider.Events;

namespace ArtistArt
{
    public interface IArtistArtService
    {
        ArtistArtSettings? Settings { get; }
        void Select(Artist artist, ArtType type, string file);
        void Reset(Artist artist, ArtType type);
    }

    public class ArtistArtService : IArtistArtService,
                                    IHandle<MediaCoversUpdatedEvent>,
                                    IHandle<ArtistRefreshCompleteEvent>,
                                    IHandle<ApplicationStartedEvent>,
                                    IHandle<ProviderUpdatedEvent<IMetadata>>
    {
        public const string LinkName = "Artwork";

        private readonly IMetadataFactory _metadataFactory;
        private readonly IArtworkStore _store;
        private readonly IArtistService _artistService;
        private readonly IArtistMetadataRepository _artistMetadataRepository;
        private readonly IConfigService _configService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public ArtistArtService(IMetadataFactory metadataFactory,
                                IArtworkStore store,
                                IArtistService artistService,
                                IArtistMetadataRepository artistMetadataRepository,
                                IConfigService configService,
                                IEventAggregator eventAggregator,
                                Logger logger)
        {
            _metadataFactory = metadataFactory;
            _store = store;
            _artistService = artistService;
            _artistMetadataRepository = artistMetadataRepository;
            _configService = configService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public ArtistArtSettings? Settings =>
            _metadataFactory.Enabled().OfType<ArtistArtProvider>().FirstOrDefault()?.CurrentSettings;

        public void Select(Artist artist, ArtType type, string file)
        {
            var settings = RequireSettings();
            _store.Select(artist, type, file, settings);
            RefreshLidarrCovers(artist, settings);
        }

        public void Reset(Artist artist, ArtType type)
        {
            var settings = RequireSettings();
            _store.Reset(artist, type, settings);
            _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(artist, true));
        }

        public void Handle(MediaCoversUpdatedEvent message)
        {
            var settings = Settings;

            // Album cover events and artists without a folder yet are not ours to touch
            if (settings == null || message.Artist == null || message.Artist.Path.IsNullOrWhiteSpace() || !Directory.Exists(message.Artist.Path))
            {
                return;
            }

            try
            {
                // Lidarr just refreshed its covers: keep its images as candidates, then put the chosen ones back.
                // Re-announce when we changed the cache so open browsers show our image, not Lidarr's.
                _store.CaptureLidarrImages(message.Artist, settings);

                if (_store.ApplyToCache(message.Artist, settings))
                {
                    _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(message.Artist, true));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Couldn't apply chosen artwork for {0}", message.Artist);
            }
        }

        public void Handle(ArtistRefreshCompleteEvent message) => EnsureLink(message.Artist, Settings);

        public void Handle(ApplicationStartedEvent message) => EnsureLinks();

        public void Handle(ProviderUpdatedEvent<IMetadata> message)
        {
            if (message.Definition.Implementation == nameof(ArtistArtProvider))
            {
                EnsureLinks();
            }
        }

        private void EnsureLinks()
        {
            var settings = Settings;

            if (settings == null || !settings.AddArtistLink)
            {
                return;
            }

            foreach (var artist in _artistService.GetAllArtists())
            {
                EnsureLink(artist, settings);
            }
        }

        private void EnsureLink(Artist artist, ArtistArtSettings? settings)
        {
            var baseUrl = PickerBaseUrl(settings);

            if (settings == null || !settings.AddArtistLink || baseUrl == null)
            {
                return;
            }

            try
            {
                var metadata = artist.Metadata.Value;
                var url = $"{baseUrl}/artwork/{artist.Id}";

                if (metadata.Links.Any(l => l.Name == LinkName && l.Url == url))
                {
                    return;
                }

                // A metadata refresh replaces the links, so this runs again after every refresh
                metadata.Links = metadata.Links.Where(l => l.Name != LinkName).Append(new Links { Name = LinkName, Url = url }).ToList();
                _artistMetadataRepository.Update(metadata);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't add the Artwork link for {0}", artist);
            }
        }

        private void RefreshLidarrCovers(Artist artist, ArtistArtSettings settings)
        {
            _store.ApplyToCache(artist, settings);

            // Lidarr pushes the new cover to open browsers when it sees this event
            _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(artist, true));
        }

        private string? PickerBaseUrl(ArtistArtSettings? settings)
        {
            var url = settings?.PickerBaseUrl;

            if (url.IsNullOrWhiteSpace())
            {
                url = _configService.ApplicationUrl;
            }

            return url.IsNullOrWhiteSpace() ? null : url!.TrimEnd('/');
        }

        private ArtistArtSettings RequireSettings() =>
            Settings ?? throw new InvalidOperationException("Enable Artist Art under Settings > Metadata first");
    }
}
