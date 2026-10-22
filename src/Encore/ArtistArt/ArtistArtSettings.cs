using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Encore.ArtistArt
{
    public class ArtistArtSettingsValidator : AbstractValidator<ArtistArtSettings>
    {
        public ArtistArtSettingsValidator()
        {
            RuleFor(c => c.CandidateFolder)
                .NotEmpty()
                .Must(f => f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                .WithMessage("Must be a single folder name, such as Artwork");

            RuleFor(c => c.PickerBaseUrl)
                .Must(u => string.IsNullOrWhiteSpace(u) || Uri.TryCreate(u, UriKind.Absolute, out _))
                .WithMessage("Must be a full address, such as http://192.168.1.10:8686");
        }
    }

    public class ArtistArtSettings : IProviderConfig
    {
        private static readonly ArtistArtSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Candidate Folder", HelpText = "Folder inside each artist folder that holds the images to choose from")]
        public string CandidateFolder { get; set; } = "Artwork";

        [FieldDefinition(1, Label = "Write artist.jpg", Type = FieldType.Checkbox, HelpText = "Write the chosen artist image as artist.jpg, the file Navidrome reads")]
        public bool WriteArtistFile { get; set; } = true;

        [FieldDefinition(2, Label = "Write folder.jpg", Type = FieldType.Checkbox, HelpText = "Also write it as folder.jpg, the file Plex, Jellyfin and Kodi read")]
        public bool WriteFolderFile { get; set; } = true;

        [FieldDefinition(3, Label = "Write Other Images", Type = FieldType.Checkbox, HelpText = "Write chosen backgrounds, banners and logos as fanart.jpg, banner.jpg and logo.png")]
        public bool WriteMediaServerFiles { get; set; } = true;

        [FieldDefinition(4, Label = "Keep Lidarr's Images", Type = FieldType.Checkbox, HelpText = "Save Lidarr's own images into the candidate folder (as poster-lidarr.jpg etc.) so you can always switch back")]
        public bool SaveLidarrImages { get; set; } = true;

        [FieldDefinition(5, Label = "Artwork Link", Type = FieldType.Checkbox, HelpText = "Add an 'Artwork' link to each artist page that opens the image picker")]
        public bool AddArtistLink { get; set; } = true;

        [FieldDefinition(6, Label = "Picker Address", Type = FieldType.Url, Advanced = true, HelpText = "Address used for the artist page link. Leave empty to use Lidarr's Application URL (Settings > General)", Placeholder = "http://192.168.1.10:8686")]
        public string PickerBaseUrl { get; set; } = string.Empty;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
