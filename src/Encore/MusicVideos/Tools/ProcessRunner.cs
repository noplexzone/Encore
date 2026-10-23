using System.Diagnostics;

namespace Encore.MusicVideos.Tools
{
    public record ProcessResult(int ExitCode, List<string> Output, List<string> Errors)
    {
        public string ErrorSummary()
        {
            var lines = Errors.Where(l => l.Contains("ERROR", StringComparison.Ordinal)).ToList();
            return string.Join(Environment.NewLine, (lines.Count > 0 ? lines : Errors).TakeLast(5)).Trim();
        }
    }

    public static class ProcessRunner
    {
        public static ProcessResult Run(string fileName,
                                        IEnumerable<string> arguments,
                                        TimeSpan timeout,
                                        CancellationToken cancellationToken,
                                        Action<string>? onOutput = null)
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var output = new List<string>();
            var errors = new List<string>();

            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lock (output)
                {
                    output.Add(e.Data);
                }

                onOutput?.Invoke(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lock (errors)
                {
                    // Keep the tail only; yt-dlp can be chatty on long playlists
                    if (errors.Count > 200)
                    {
                        errors.RemoveAt(0);
                    }

                    errors.Add(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                process.WaitForExitAsync(timeoutSource.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }

                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"{Path.GetFileName(fileName)} took longer than {timeout.TotalMinutes:0} minutes");
            }

            // Flush the async readers
            process.WaitForExit();

            return new ProcessResult(process.ExitCode, output, errors);
        }
    }
}
