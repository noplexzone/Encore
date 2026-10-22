using Encore.MusicVideos.Videos;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Encore.MusicVideos
{
    public enum VideoContainer
    {
        [FieldOption(Label = "MKV (holds any codec, keeps the best streams)")]
        Mkv = 0,

        [FieldOption(Label = "MP4")]
        Mp4 = 1
    }

    public enum VideoCodecPreference
    {
        [FieldOption(Label = "Best quality (AV1 or VP9 when better)")]
        BestQuality = 0,

        [FieldOption(Label = "Most compatible (H.264 and AAC, usually up to 1080p)")]
        Compatible = 1
    }

    public enum MaxResolution
    {
        [FieldOption(Label = "No limit")]
        Any = 0,

        [FieldOption(Label = "2160p (4K)")]
        P2160 = 2160,

        [FieldOption(Label = "1440p")]
        P1440 = 1440,

        [FieldOption(Label = "1080p")]
        P1080 = 1080,

        [FieldOption(Label = "720p")]
        P720 = 720
    }

    public class MusicVideosSettingsValidator : AbstractValidator<MusicVideosSettings>
    {
        public MusicVideosSettingsValidator()
        {
            RuleFor(c => c.VideoFolder)
                .Must(f => string.IsNullOrEmpty(f) || f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                .WithMessage("Must be a single folder name, such as Music Videos, or empty for the artist folder");

            RuleFor(c => c.FileNameFormat)
                .NotEmpty()
                .Must(f => f.Contains(VideoNaming.TitleToken, StringComparison.OrdinalIgnoreCase))
                .WithMessage($"Must contain {VideoNaming.TitleToken}");

            RuleFor(c => c.SearchIntervalDays).InclusiveBetween(1, 365);

            RuleFor(c => c.LinkBaseUrl)
                .Must(u => string.IsNullOrWhiteSpace(u) || Uri.TryCreate(u, UriKind.Absolute, out _))
                .WithMessage("Must be a full address, such as http://192.168.1.10:8686");
        }
    }

    public class MusicVideosSettings : IProviderConfig
    {
        private static readonly MusicVideosSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Video Folder", HelpText = "Folder inside each artist folder for videos. Leave empty to put them straight in the artist folder (what Plex expects)")]
        public string VideoFolder { get; set; } = "Music Videos";

        [FieldDefinition(1, Label = "File Name", HelpText = "Tokens: {Artist Name}, {Track Title}, {Video Type}, {Year}. For Plex use {Track Title}-video")]
        public string FileNameFormat { get; set; } = "{Artist Name} - {Track Title}";

        [FieldDefinition(2, Label = "Container", Type = FieldType.Select, SelectOptions = typeof(VideoContainer), HelpText = "Never .webm, which music servers like Navidrome treat as audio")]
        public int Container { get; set; } = (int)VideoContainer.Mkv;

        [FieldDefinition(3, Label = "Codecs", Type = FieldType.Select, SelectOptions = typeof(VideoCodecPreference), HelpText = "Best quality picks the highest resolution and bitrate. YouTube only has 1440p and 4K as AV1 or VP9")]
        public int CodecPreference { get; set; } = (int)VideoCodecPreference.BestQuality;

        [FieldDefinition(4, Label = "Max Resolution", Type = FieldType.Select, SelectOptions = typeof(MaxResolution))]
        public int MaxResolution { get; set; } = (int)MusicVideos.MaxResolution.Any;

        [FieldDefinition(5, Label = "Download Automatically", Type = FieldType.Checkbox, HelpText = "Download videos as soon as they are found. When off, found videos wait on the Videos page")]
        public bool AutoDownload { get; set; } = true;

        [FieldDefinition(6, Label = "Live Videos", Type = FieldType.Checkbox, HelpText = "Also get live performance videos")]
        public bool IncludeLive { get; set; }

        [FieldDefinition(7, Label = "Lyric Videos", Type = FieldType.Checkbox, HelpText = "Also get lyric videos")]
        public bool IncludeLyric { get; set; }

        [FieldDefinition(8, Label = "Only Tracks in Library", Type = FieldType.Checkbox, HelpText = "Only get videos for songs that are in Lidarr")]
        public bool OnlyLibraryTracks { get; set; }

        [FieldDefinition(9, Label = "Write NFO", Type = FieldType.Checkbox, HelpText = "Write a .nfo next to each video for Kodi, Jellyfin and Emby")]
        public bool WriteNfo { get; set; } = true;

        [FieldDefinition(10, Label = "Save Thumbnail", Type = FieldType.Checkbox, HelpText = "Save the video thumbnail as <name>-thumb.jpg")]
        public bool SaveThumbnail { get; set; } = true;

        [FieldDefinition(11, Label = "Videos Link", Type = FieldType.Checkbox, HelpText = "Add a 'Videos' link to each artist page")]
        public bool AddArtistLink { get; set; } = true;

        [FieldDefinition(12, Label = "Search Every (days)", Type = FieldType.Number, Advanced = true, HelpText = "How often each artist is checked for new videos")]
        public int SearchIntervalDays { get; set; } = 7;

        [FieldDefinition(13, Label = "Cookies File", Type = FieldType.FilePath, Advanced = true, HelpText = "Netscape cookies.txt from a signed-in YouTube account. Lets yt-dlp get Premium-only higher bitrate formats and age-restricted videos")]
        public string CookiesFile { get; set; } = string.Empty;

        [FieldDefinition(14, Label = "yt-dlp Path", Type = FieldType.FilePath, Advanced = true, HelpText = "Leave empty to let Encore download yt-dlp and keep it updated")]
        public string YtDlpPath { get; set; } = string.Empty;

        [FieldDefinition(15, Label = "FFmpeg Path", Type = FieldType.FilePath, Advanced = true, HelpText = "ffmpeg binary or its folder. Leave empty to use the one on PATH, or let Encore download one")]
        public string FfmpegPath { get; set; } = string.Empty;

        [FieldDefinition(16, Label = "Extra yt-dlp Arguments", Advanced = true, HelpText = "Added to every download, for example --limit-rate 5M")]
        public string ExtraArguments { get; set; } = string.Empty;

        [FieldDefinition(17, Label = "Link Address", Type = FieldType.Url, Advanced = true, HelpText = "Address used for the artist page link. Leave empty to use Lidarr's Application URL (Settings > General)", Placeholder = "http://192.168.1.10:8686")]
        public string LinkBaseUrl { get; set; } = string.Empty;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
