using System.Globalization;
using System.Text;
using System.Text.Json;
using Encore.MusicVideos.Videos;

namespace Encore.MusicVideos.Tools
{
    public record ListedVideo(string Id, string Url, string Title, int? Duration, string? Channel);

    public record DownloadedVideo(string File, string? Thumbnail, VideoQuality Quality, string? Title, int? Year, int? Duration);

    /// <summary>
    /// Builds yt-dlp command lines and reads what it prints. Everything that talks to
    /// YouTube goes through yt-dlp, so YouTube changes are fixed by updating yt-dlp.
    /// </summary>
    public static class YtDlp
    {
        public const string ResultPrefix = "ENCORE_RESULT ";
        public const string EntryPrefix = "ENCORE_ENTRY ";
        public const string ProgressPrefix = "ENCORE_PROGRESS ";

        private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromHours(2);

        public static List<string> CommonArguments(ToolPaths tools, MusicVideosSettings settings)
        {
            var args = new List<string> { "--ignore-config", "--no-colors", "--newline" };

            if (tools.Ffmpeg != null)
            {
                args.AddRange(["--ffmpeg-location", tools.Ffmpeg]);
            }

            if (tools.JsRuntime != null)
            {
                args.AddRange(["--js-runtimes", tools.JsRuntime]);
            }

            if (!string.IsNullOrWhiteSpace(settings.CookiesFile))
            {
                args.AddRange(["--cookies", settings.CookiesFile]);
            }

            args.AddRange(SplitArguments(settings.ExtraArguments));
            return args;
        }

        /// <summary>
        /// The -S format sort. Best quality takes the highest resolution, then frame rate and HDR, then
        /// VP9 over AV1 and the highest video bitrate. YouTube's high bitrate VP9 streams (616 at 1080p,
        /// 625 at 4K) carry three to six times the bitrate of its AV1 streams, which yt-dlp's default
        /// codec order would otherwise pick. Compatible prefers H.264 and AAC, as yt-dlp's -t mp4 does.
        /// </summary>
        public static string FormatSort(MusicVideosSettings settings)
        {
            var resolution = settings.MaxResolution > 0 ? $"res:{settings.MaxResolution}" : "res";

            return (VideoCodecPreference)settings.CodecPreference == VideoCodecPreference.Compatible
                ? $"vcodec:h264,{resolution},fps,acodec:aac"
                : $"{resolution},fps,hdr:12,vcodec:vp9,vbr";
        }

        public static string ContainerExtension(MusicVideosSettings settings) =>
            (VideoContainer)settings.Container == VideoContainer.Mp4 ? "mp4" : "mkv";

        public static List<string> DownloadArguments(ToolPaths tools, MusicVideosSettings settings, string url, string tempFolder)
        {
            var container = ContainerExtension(settings);
            var args = CommonArguments(tools, settings);

            args.AddRange(["--no-playlist", "--no-mtime", "-f", "bv*+ba/b"]);

            args.AddRange(["-S", FormatSort(settings)]);

            // Never leave a .webm behind: Navidrome would list it as a song
            args.AddRange(["--merge-output-format", container, "--remux-video", container]);
            args.AddRange(["--embed-metadata", "--embed-chapters"]);
            args.AddRange(["-o", Path.Combine(tempFolder, "video.%(ext)s")]);

            if (settings.SaveThumbnail)
            {
                args.AddRange(["--write-thumbnail", "--convert-thumbnails", "jpg", "-o", "thumbnail:" + Path.Combine(tempFolder, "thumb.%(ext)s")]);
            }

            args.AddRange(["--progress", "--progress-template", "download:" + ProgressPrefix + "%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s"]);
            args.AddRange(["--print", "after_move:" + ResultPrefix + "%(.{filepath,width,height,fps,vcodec,acodec,abr,vbr,filesize,filesize_approx,title,upload_date,release_year,duration})j"]);
            args.Add("--");
            args.Add(url);

            return args;
        }

        public static List<string> ListArguments(ToolPaths tools, MusicVideosSettings settings, string url, int limit)
        {
            var args = CommonArguments(tools, settings);
            args.AddRange(["--flat-playlist", "--ignore-errors", "--playlist-end", limit.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["--print", EntryPrefix + "%(.{id,url,title,duration,channel,uploader})j", "--", url]);
            return args;
        }

        public static List<ListedVideo> List(ToolPaths tools, MusicVideosSettings settings, string url, int limit, CancellationToken cancellationToken)
        {
            var result = ProcessRunner.Run(tools.YtDlp, ListArguments(tools, settings, url, limit), ListTimeout, cancellationToken);
            var entries = ParseEntries(result.Output);

            if (entries.Count == 0 && result.ExitCode != 0)
            {
                throw new InvalidOperationException($"yt-dlp couldn't read {url}: {result.ErrorSummary()}");
            }

            return entries;
        }

        public static DownloadedVideo Download(ToolPaths tools,
                                               MusicVideosSettings settings,
                                               string url,
                                               string tempFolder,
                                               Action<string> onProgress,
                                               CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(tempFolder);

            var result = ProcessRunner.Run(tools.YtDlp, DownloadArguments(tools, settings, url, tempFolder), DownloadTimeout, cancellationToken, line =>
            {
                if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                {
                    onProgress(FormatProgress(line[ProgressPrefix.Length..]));
                }
            });

            var line = result.Output.LastOrDefault(l => l.StartsWith(ResultPrefix, StringComparison.Ordinal));

            if (result.ExitCode != 0 || line == null)
            {
                var summary = result.ErrorSummary();
                throw new InvalidOperationException(summary.Length > 0 ? summary : $"yt-dlp exited with code {result.ExitCode}");
            }

            var downloaded = ParseResult(line[ResultPrefix.Length..]);
            var thumbnail = Path.Combine(tempFolder, "thumb.jpg");

            // Sites other than YouTube don't always say what they sent
            if (downloaded.Quality.Height == null || downloaded.Quality.VideoCodec == null)
            {
                Probe(tools, downloaded.File, downloaded.Quality, cancellationToken);
            }

            return downloaded with { Thumbnail = File.Exists(thumbnail) ? thumbnail : null };
        }

        public static List<ListedVideo> ParseEntries(IEnumerable<string> lines)
        {
            var entries = new List<ListedVideo>();

            foreach (var line in lines.Where(l => l.StartsWith(EntryPrefix, StringComparison.Ordinal)))
            {
                try
                {
                    using var json = JsonDocument.Parse(line[EntryPrefix.Length..]);
                    var root = json.RootElement;
                    var id = String(root, "id");
                    var title = String(root, "title");

                    if (id == null || title == null)
                    {
                        continue;
                    }

                    var url = String(root, "url") ?? id;
                    if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        url = YouTube.WatchUrl(id);
                    }

                    var duration = Number(root, "duration");
                    entries.Add(new ListedVideo(id, url, title, duration == null ? null : (int)duration, String(root, "channel") ?? String(root, "uploader")));
                }
                catch (JsonException)
                {
                }
            }

            return entries;
        }

        public static DownloadedVideo ParseResult(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var quality = new VideoQuality
            {
                Width = (int?)Number(root, "width"),
                Height = (int?)Number(root, "height"),
                Fps = Number(root, "fps"),
                VideoCodec = Codec(String(root, "vcodec")),
                AudioCodec = Codec(String(root, "acodec")),
                AudioBitrate = Number(root, "abr"),
                VideoBitrate = Number(root, "vbr"),
                Size = (long?)(Number(root, "filesize") ?? Number(root, "filesize_approx"))
            };

            var file = String(root, "filepath") ?? throw new InvalidOperationException("yt-dlp didn't say where it saved the video");

            if (File.Exists(file))
            {
                quality.Size = new FileInfo(file).Length;
            }

            var uploadDate = String(root, "upload_date");
            var year = (int?)Number(root, "release_year") ??
                       (uploadDate?.Length >= 4 && int.TryParse(uploadDate[..4], out var y) ? y : null);
            var duration = Number(root, "duration");

            return new DownloadedVideo(file, null, quality, String(root, "title"), year, duration == null ? null : (int)Math.Round(duration.Value));
        }

        /// <summary>
        /// "  4.1%|  13.39MiB/s|00:00" becomes "4.1% · 13.39MiB/s · 00:00".
        /// </summary>
        public static string FormatProgress(string progress) =>
            string.Join(" · ", progress.Split('|')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0 && p != "NA" && !p.StartsWith("Unknown", StringComparison.Ordinal)));

        /// <summary>
        /// Fills in what yt-dlp didn't report from the file itself, using ffprobe next to ffmpeg.
        /// </summary>
        public static void Probe(ToolPaths tools, string file, VideoQuality quality, CancellationToken cancellationToken)
        {
            var ffprobe = tools.Ffmpeg == null ? "ffprobe" : Path.Combine(Path.GetDirectoryName(tools.Ffmpeg)!, Path.GetFileName(tools.Ffmpeg).Replace("ffmpeg", "ffprobe"));

            try
            {
                var result = ProcessRunner.Run(ffprobe, ["-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height,avg_frame_rate,bit_rate", "-of", "json", file], TimeSpan.FromMinutes(1), cancellationToken);

                if (result.ExitCode == 0)
                {
                    ApplyProbe(string.Join("\n", result.Output), quality);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or JsonException or TimeoutException)
            {
                // Quality details are nice to have; the video is fine without them
            }
        }

        public static void ApplyProbe(string json, VideoQuality quality)
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("streams", out var streams))
            {
                return;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                var type = String(stream, "codec_type");

                if (type == "video" && quality.VideoCodec == null)
                {
                    quality.VideoCodec = String(stream, "codec_name");
                    quality.Width ??= (int?)Number(stream, "width");
                    quality.Height ??= (int?)Number(stream, "height");

                    var rate = String(stream, "avg_frame_rate")?.Split('/');
                    if (quality.Fps == null && rate?.Length == 2 && double.TryParse(rate[0], CultureInfo.InvariantCulture, out var n) &&
                        double.TryParse(rate[1], CultureInfo.InvariantCulture, out var d) && d > 0)
                    {
                        quality.Fps = Math.Round(n / d, 2);
                    }
                }
                else if (type == "audio" && quality.AudioCodec == null)
                {
                    quality.AudioCodec = String(stream, "codec_name");

                    if (quality.AudioBitrate == null && double.TryParse(String(stream, "bit_rate"), CultureInfo.InvariantCulture, out var bitrate))
                    {
                        quality.AudioBitrate = bitrate / 1000;
                    }
                }
            }
        }

        /// <summary>
        /// Splits "--limit-rate 5M --sleep-interval 2 -o 'a b'" like a shell would, without running one.
        /// </summary>
        public static List<string> SplitArguments(string? arguments)
        {
            var result = new List<string>();

            if (string.IsNullOrWhiteSpace(arguments))
            {
                return result;
            }

            var current = new StringBuilder();
            char? quote = null;
            var hasToken = false;

            foreach (var c in arguments)
            {
                if (quote != null)
                {
                    if (c == quote)
                    {
                        quote = null;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                    hasToken = true;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (hasToken)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }
                }
                else
                {
                    current.Append(c);
                    hasToken = true;
                }
            }

            if (hasToken)
            {
                result.Add(current.ToString());
            }

            return result;
        }

        private static string? Codec(string? codec) => codec is null or "none" ? null : codec;

        private static string? String(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static double? Number(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }
}
