using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;

namespace ArtistArt
{
    /// <summary>
    /// Shows up under Settings > Metadata. Enabling it turns the plugin on; it writes
    /// no metadata files through Lidarr's own pipeline.
    /// </summary>
    public class ArtistArtProvider : MetadataBase<ArtistArtSettings>
    {
        public override string Name => "Artist Art";

        public override ProviderMessage Message => new(
            "Choose between several images per artist. Put images in each artist's Artwork folder (or upload them), " +
            "then open the Artwork link on the artist page, or go to /artwork on this Lidarr address.",
            ProviderMessageType.Info);

        public override MetadataFile FindMetadataFile(Artist artist, string path) => null!;

        public override MetadataFileResult ArtistMetadata(Artist artist) => null!;

        public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => null!;

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => null!;

        public override List<ImageFileResult> ArtistImages(Artist artist) => [];

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => [];

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => [];

        public ArtistArtSettings CurrentSettings => Settings;
    }
}
