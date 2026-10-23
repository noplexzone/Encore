using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;

namespace Encore.MusicVideos
{
    /// <summary>
    /// Shows up under Settings > Metadata. Enabling it turns music videos on; it writes
    /// no metadata files through Lidarr's own pipeline.
    /// </summary>
    public class MusicVideosProvider : MetadataBase<MusicVideosSettings>
    {
        public override string Name => "Music Videos";

        public override ProviderMessage Message => new(
            "Finds official music videos for each artist (MusicBrainz and the artist's YouTube channels) and downloads them " +
            "with yt-dlp into the artist folder. Open the Videos link on an artist page, or go to /videos on this Lidarr address.",
            ProviderMessageType.Info);

        public override MetadataFile FindMetadataFile(Artist artist, string path) => null!;

        public override MetadataFileResult ArtistMetadata(Artist artist) => null!;

        public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => null!;

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => null!;

        public override List<ImageFileResult> ArtistImages(Artist artist) => [];

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumPath) => [];

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => [];

        public MusicVideosSettings CurrentSettings => Settings;
    }
}
