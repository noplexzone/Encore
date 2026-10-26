using System.IO.Compression;
using System.Runtime.InteropServices;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;

namespace Encore.MusicVideos.Tools
{
    public record ToolPaths(string YtDlp, string? Ffmpeg, string? JsRuntime);

    public interface IVideoTools
    {
        /// <summary>
        /// Finds yt-dlp, ffmpeg and a JavaScript runtime, downloading what's missing.
        /// Throws when yt-dlp can't be found or installed.
        /// </summary>
        ToolPaths Resolve(MusicVideosSettings settings, CancellationToken cancellationToken);

        string? LastProblem { get; }
    }

    /// <summary>
    /// yt-dlp needs ffmpeg to merge the separate best video and audio streams, and since late 2025 a
    /// JavaScript runtime (deno by default) to get YouTube's full format list. Encore keeps its own
    /// copies under &lt;Lidarr data&gt;/Encore/bin unless the settings point somewhere else.
    /// </summary>
    public class VideoTools : IVideoTools
    {
        private static readonly TimeSpan UpdateEvery = TimeSpan.FromHours(24);
        private static readonly object Lock = new();

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly string _binFolder;
        private (DateTime At, string Message)? _ytDlpFailure;
        private (DateTime At, string Message)? _jsFailure;

        public VideoTools(IHttpClient httpClient, IAppFolderInfo appFolderInfo, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _binFolder = Path.Combine(appFolderInfo.AppDataFolder, "Encore", "bin");
        }

        public string? LastProblem { get; private set; }

        private static bool IsWindows => OsInfo.IsWindows;
        private static string Exe(string name) => IsWindows ? name + ".exe" : name;
        private static Architecture Arch => RuntimeInformation.OSArchitecture;

        public ToolPaths Resolve(MusicVideosSettings settings, CancellationToken cancellationToken)
        {
            lock (Lock)
            {
                // Don't retry a failed yt-dlp install for every queued job
                if (_ytDlpFailure != null && _ytDlpFailure.Value.At > DateTime.UtcNow.AddMinutes(-30))
                {
                    throw new InvalidOperationException(_ytDlpFailure.Value.Message);
                }

                var problems = new List<string>();
                string ytDlp;

                try
                {
                    ytDlp = ResolveYtDlp(settings, cancellationToken);
                    _ytDlpFailure = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = $"yt-dlp isn't available: {ex.Message}";
                    _ytDlpFailure = (DateTime.UtcNow, message);
                    LastProblem = message;
                    throw new InvalidOperationException(message, ex);
                }

                var ffmpeg = Try(() => ResolveFfmpeg(settings), problems);
                var js = Try(ResolveJsRuntime, problems);

                LastProblem = problems.Count > 0 ? string.Join(" ", problems) : null;
                return new ToolPaths(ytDlp, ffmpeg, js);
            }
        }

        private string? Try(Func<string?> resolve, List<string> problems)
        {
            try
            {
                return resolve();
            }
            catch (Exception ex)
            {
                if (ex is not RepeatedProblemException)
                {
                    _logger.Warn("Music videos: {0}", ex.Message);
                }

                problems.Add(ex.Message);
                return null;
            }
        }

        // A problem already logged
        private class RepeatedProblemException(string message) : Exception(message);

        private string ResolveYtDlp(MusicVideosSettings settings, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(settings.YtDlpPath))
            {
                return File.Exists(settings.YtDlpPath)
                    ? settings.YtDlpPath
                    : throw new FileNotFoundException($"yt-dlp not found at {settings.YtDlpPath}");
            }

            var path = Path.Combine(_binFolder, Exe("yt-dlp"));
            var stamp = path + ".checked";

            if (!File.Exists(path))
            {
                Download($"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{YtDlpAsset()}", path);
                MakeExecutable(path);
                File.WriteAllText(stamp, string.Empty);
                return path;
            }

            // YouTube changes often, and an old yt-dlp is the usual reason downloads break
            if (!File.Exists(stamp) || File.GetLastWriteTimeUtc(stamp) < DateTime.UtcNow - UpdateEvery)
            {
                try
                {
                    var result = ProcessRunner.Run(path, ["--update-to", "stable"], TimeSpan.FromMinutes(5), cancellationToken);
                    _logger.Debug("yt-dlp update: {0}", string.Join(" ", result.Output.Concat(result.Errors)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warn(ex, "Couldn't update yt-dlp");
                }

                File.WriteAllText(stamp, string.Empty);
            }

            return path;
        }

        private string ResolveFfmpeg(MusicVideosSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.FfmpegPath))
            {
                var configured = Directory.Exists(settings.FfmpegPath) ? Path.Combine(settings.FfmpegPath, Exe("ffmpeg")) : settings.FfmpegPath;
                return File.Exists(configured) ? configured : throw new FileNotFoundException($"ffmpeg not found at {settings.FfmpegPath}");
            }

            var onPath = FindOnPath("ffmpeg");
            if (onPath != null)
            {
                return onPath;
            }

            var local = Path.Combine(_binFolder, "ffmpeg", Exe("ffmpeg"));
            if (File.Exists(local))
            {
                return local;
            }

            var build = FfmpegBuild() ?? throw new PlatformNotSupportedException(
                "ffmpeg is needed to join the best video and audio streams. Install it (for example 'brew install ffmpeg') or set FFmpeg Path.");

            var archive = Path.Combine(_binFolder, build);
            Download($"https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/{build}", archive);

            try
            {
                var extractTo = Path.Combine(_binFolder, "ffmpeg-extract");
                Extract(archive, extractTo);

                var folder = Path.GetDirectoryName(local)!;
                Directory.CreateDirectory(folder);

                foreach (var tool in new[] { "ffmpeg", "ffprobe" })
                {
                    var found = Directory.EnumerateFiles(extractTo, Exe(tool), SearchOption.AllDirectories).FirstOrDefault()
                                ?? throw new FileNotFoundException($"{tool} was not in {build}");
                    var target = Path.Combine(folder, Exe(tool));
                    File.Move(found, target, true);
                    MakeExecutable(target);
                }

                Directory.Delete(extractTo, true);
            }
            finally
            {
                File.Delete(archive);
            }

            return local;
        }

        private string? ResolveJsRuntime()
        {
            foreach (var runtime in new[] { "deno", "node", "bun", "qjs" })
            {
                var found = FindOnPath(runtime);
                if (found != null)
                {
                    return $"{(runtime == "qjs" ? "quickjs" : runtime)}:{found}";
                }
            }

            foreach (var runtime in new[] { "deno", "bun" })
            {
                var installed = Path.Combine(_binFolder, Exe(runtime));
                if (File.Exists(installed))
                {
                    return $"{runtime}:{installed}";
                }
            }

            // A failed install is retried once a day, not for every artist
            if (_jsFailure != null && _jsFailure.Value.At > DateTime.UtcNow - UpdateEvery)
            {
                throw new RepeatedProblemException(_jsFailure.Value.Message);
            }

            try
            {
                return InstallJsRuntime();
            }
            catch (Exception ex)
            {
                _jsFailure = (DateTime.UtcNow, ex.Message);
                throw;
            }
        }

        private string InstallJsRuntime()
        {
            var (runtime, url) = JsRuntimeDownload() ?? throw new PlatformNotSupportedException(
                "No JavaScript runtime found, so YouTube may only offer low quality formats. Install deno, node or bun.");

            var local = Path.Combine(_binFolder, Exe(runtime));
            var archive = local + ".zip";
            Download(url, archive);

            try
            {
                using var zip = ZipFile.OpenRead(archive);
                var entry = zip.Entries.First(e => e.Name == Exe(runtime));
                entry.ExtractToFile(local, true);
                MakeExecutable(local);
            }
            finally
            {
                File.Delete(archive);
            }

            // Bun's musl build needs libstdc++, which Docker images that run .NET already have
            ProcessResult check;

            try
            {
                check = ProcessRunner.Run(local, ["--version"], TimeSpan.FromSeconds(30), CancellationToken.None);
            }
            catch (Exception ex)
            {
                check = new ProcessResult(-1, [], [ex.Message]);
            }

            if (check.ExitCode != 0)
            {
                File.Delete(local);
                throw new PlatformNotSupportedException(
                    $"{runtime} doesn't run here ({check.ErrorSummary()}), so YouTube may only offer low quality formats. " +
                    "Install node (on Alpine-based Docker images: apk add nodejs).");
            }

            _logger.Info("Music videos: installed {0} {1}", runtime, string.Join(" ", check.Output).Trim());
            return $"{runtime}:{local}";
        }

        private void Download(string url, string path)
        {
            _logger.Info("Downloading {0}", url);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _httpClient.DownloadFile(url, path);
        }

        private static void Extract(string archive, string destination)
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }

            Directory.CreateDirectory(destination);

            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archive, destination);
                return;
            }

            // .NET has no xz support, so use the system tar
            var result = ProcessRunner.Run("tar", ["-xJf", archive, "-C", destination], TimeSpan.FromMinutes(5), CancellationToken.None);

            if (result.ExitCode != 0)
            {
                throw new IOException($"Couldn't unpack {Path.GetFileName(archive)}: {result.ErrorSummary()}. Install ffmpeg or set FFmpeg Path instead.");
            }
        }

        private static void MakeExecutable(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
        }

        private static string? FindOnPath(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(folder, Exe(name));

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string YtDlpAsset() => (OsInfo.Os, Arch) switch
        {
            (Os.Windows, Architecture.Arm64) => "yt-dlp_arm64.exe",
            (Os.Windows, Architecture.X86) => "yt-dlp_x86.exe",
            (Os.Windows, _) => "yt-dlp.exe",
            (Os.Osx, _) => "yt-dlp_macos",
            (Os.LinuxMusl, Architecture.Arm64) => "yt-dlp_musllinux_aarch64",
            (Os.LinuxMusl, _) => "yt-dlp_musllinux",
            (Os.Linux, Architecture.Arm64) => "yt-dlp_linux_aarch64",
            (Os.Linux, Architecture.Arm) => "yt-dlp_linux_armv7l",
            (Os.Linux, _) => "yt-dlp_linux",
            _ => throw new PlatformNotSupportedException("No yt-dlp build for this system. Install yt-dlp and set yt-dlp Path.")
        };

        private static string? FfmpegBuild() => (OsInfo.Os, Arch) switch
        {
            (Os.Windows, Architecture.Arm64) => "ffmpeg-master-latest-winarm64-gpl.zip",
            (Os.Windows, _) => "ffmpeg-master-latest-win64-gpl.zip",
            (Os.Linux or Os.LinuxMusl, Architecture.Arm64) => "ffmpeg-master-latest-linuxarm64-gpl.tar.xz",
            (Os.Linux or Os.LinuxMusl, Architecture.X64) => "ffmpeg-master-latest-linux64-gpl.tar.xz",
            _ => null
        };

        // Deno only publishes glibc builds for Linux, so musl (Alpine, as in most Lidarr Docker images) gets bun.
        // Bun's baseline build also runs on older CPUs without AVX2, common in NAS boxes.
        private static (string Runtime, string Url)? JsRuntimeDownload()
        {
            const string Deno = "https://github.com/denoland/deno/releases/latest/download/";
            const string Bun = "https://github.com/oven-sh/bun/releases/latest/download/";

            return (OsInfo.Os, Arch) switch
            {
                (Os.Windows, Architecture.X64) => ("deno", Deno + "deno-x86_64-pc-windows-msvc.zip"),
                (Os.Osx, Architecture.Arm64) => ("deno", Deno + "deno-aarch64-apple-darwin.zip"),
                (Os.Osx, _) => ("deno", Deno + "deno-x86_64-apple-darwin.zip"),
                (Os.Linux, Architecture.Arm64) => ("deno", Deno + "deno-aarch64-unknown-linux-gnu.zip"),
                (Os.Linux, Architecture.X64) => ("deno", Deno + "deno-x86_64-unknown-linux-gnu.zip"),
                (Os.LinuxMusl, Architecture.Arm64) => ("bun", Bun + "bun-linux-aarch64-musl.zip"),
                (Os.LinuxMusl, Architecture.X64) => ("bun", Bun + "bun-linux-x64-musl-baseline.zip"),
                _ => null
            };
        }
    }
}
