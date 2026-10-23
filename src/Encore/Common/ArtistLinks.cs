using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Music;

namespace Encore.Common
{
    /// <summary>
    /// Adds Encore's own entries (Artwork, Videos) to the Links list on Lidarr's artist page.
    /// </summary>
    public interface IArtistLinks
    {
        string? BaseUrl(string? overrideUrl);
        void Ensure(Artist artist, string name, string? url);
    }

    public class ArtistLinks : IArtistLinks
    {
        private readonly IArtistMetadataRepository _artistMetadataRepository;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public ArtistLinks(IArtistMetadataRepository artistMetadataRepository, IConfigService configService, Logger logger)
        {
            _artistMetadataRepository = artistMetadataRepository;
            _configService = configService;
            _logger = logger;
        }

        public string? BaseUrl(string? overrideUrl)
        {
            var url = overrideUrl;

            if (url.IsNullOrWhiteSpace())
            {
                url = _configService.ApplicationUrl;
            }

            return url.IsNullOrWhiteSpace() ? null : url!.TrimEnd('/');
        }

        public void Ensure(Artist artist, string name, string? url)
        {
            try
            {
                var metadata = artist.Metadata.Value;

                if (url == null ? metadata.Links.All(l => l.Name != name) : metadata.Links.Any(l => l.Name == name && l.Url == url))
                {
                    return;
                }

                // A metadata refresh replaces the links, so this runs again after every refresh
                var links = metadata.Links.Where(l => l.Name != name);
                metadata.Links = (url == null ? links : links.Append(new Links { Name = name, Url = url })).ToList();
                _artistMetadataRepository.Update(metadata);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't update the {0} link for {1}", name, artist);
            }
        }
    }
}
