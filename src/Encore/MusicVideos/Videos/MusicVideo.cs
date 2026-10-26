using System.Text.Json.Serialization;

namespace Encore.MusicVideos.Videos
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VideoKind
    {
        Official,
        Live,
        Lyric,
        Other
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VideoStatus
    {
        Wanted,
        Downloaded,

        // Only in files from before videos had Monitored; read as an unmonitored video
        Ignored,
        Failed
    }

    /// <summary>
    /// One video of an artist, found or downloaded. Key is "youtube:&lt;id&gt;" for YouTube,
    /// otherwise the page address, so the same video is never added twice.
    /// </summary>
    public class MusicVideo
    {
        public string Key { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string SourceTitle { get; set; } = string.Empty;
        public VideoKind Kind { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? Thumbnail { get; set; }
        public int? Duration { get; set; }
        public string? RecordingId { get; set; }
        public int? Year { get; set; }
        public VideoStatus Status { get; set; }

        // Like a monitored album: downloaded by an artist or section search when missing
        public bool Monitored { get; set; } = true;
        public string? File { get; set; }
        public string? Error { get; set; }
        public DateTime Added { get; set; }
        public DateTime? DownloadedAt { get; set; }
        public VideoQuality? Quality { get; set; }
    }

    public class VideoQuality
    {
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? Fps { get; set; }
        public string? VideoCodec { get; set; }
        public string? AudioCodec { get; set; }
        public double? AudioBitrate { get; set; }
        public double? VideoBitrate { get; set; }
        public long? Size { get; set; }

        public override string ToString()
        {
            var parts = new List<string>();

            if (Height != null)
            {
                parts.Add($"{Height}p" + (Fps is > 30 ? Math.Round(Fps.Value).ToString() : string.Empty));
            }

            if (VideoCodec != null)
            {
                parts.Add(VideoCodec.Split('.')[0]);
            }

            if (AudioCodec != null)
            {
                parts.Add(AudioCodec.Split('.')[0] + (AudioBitrate != null ? $" {Math.Round(AudioBitrate.Value, MidpointRounding.AwayFromZero)}k" : string.Empty));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Saved as musicvideos.json in the video folder, so it survives a Lidarr reinstall.
    /// </summary>
    public class VideoState
    {
        // When the videos were last looked for (named from when that was called a search)
        public DateTime? LastSearch { get; set; }

        // The last look had failed parts (MusicBrainz busy, say), so the next refresh shouldn't wait
        public bool Incomplete { get; set; }

        public List<MusicVideo> Videos { get; set; } = [];

        // Whether videos found from now on start out monitored, when the section's monitor toggle
        // differs from the Monitor New Videos setting. Empty follows the setting
        public bool? MonitorNewVideos { get; set; }

        // Only in files from 0.3: always written, so only false says something
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? MonitorNew { get; set; }

        // MusicBrainz video recordings already looked up, so a new search only asks about new ones
        public HashSet<string> CheckedRecordings { get; set; } = [];

        // Channels or playlists added by hand, scanned along with the ones MusicBrainz knows
        public List<string> ExtraSources { get; set; } = [];
    }
}
