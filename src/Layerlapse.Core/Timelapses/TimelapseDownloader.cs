using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Timelapses;

public enum DownloadOutcome
{
    Downloaded,

    /// <summary>Already in the destination with the same size; not downloaded again.</summary>
    SkippedExisting,

    /// <summary>A different file with the same name is in the destination; left untouched.</summary>
    SkippedConflict,

    /// <summary>Copied from the local cache (it had been played), so nothing was transferred.</summary>
    CopiedFromCache,

    Failed,

    /// <summary>Stopped by the user. The partial file is kept, and the next download continues from it.</summary>
    Cancelled,
}

/// <param name="ResumedFrom">Bytes that were already on disk when the last attempt started (0 = from scratch).</param>
/// <param name="Attempts">Transfer attempts, including retries after dropped connections.</param>
public sealed record DownloadResult(Timelapse Timelapse, DownloadOutcome Outcome, string? Path, long ResumedFrom = 0, int Attempts = 0, string? Error = null);

/// <param name="Index">Position of the current file in the batch, from 0.</param>
public sealed record DownloadProgress(int Index, int Count, Timelapse Current, long FileBytes, long BatchBytes, long BatchSize);

/// <summary>
/// Downloads timelapses into a folder the user chose, one at a time. Files already there are skipped. Each
/// file is written to "&lt;name&gt;.part" and renamed when complete, so a cancelled or dropped download
/// continues from where it stopped (FTP REST) instead of starting again. Read-only towards the printer.
/// </summary>
public sealed class TimelapseDownloader(PrinterSession session, TimelapseCache? cache = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string PartialSuffix = ".part";

    /// <summary>Attempts per file. Waits 1, 2, 4 and 8 seconds between them.</summary>
    public const int MaxAttempts = 5;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<IReadOnlyList<DownloadResult>> DownloadAsync(
        IReadOnlyList<Timelapse> timelapses,
        string folder,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var batchSize = timelapses.Sum(t => t.Size);
        long done = 0;
        var results = new List<DownloadResult>();

        for (var index = 0; index < timelapses.Count; index++)
        {
            var timelapse = timelapses[index];
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new DownloadResult(timelapse, DownloadOutcome.Cancelled, null));
                continue;
            }

            var finishedBefore = done;
            var fileProgress = new SyncProgress(bytes => progress?.Report(new DownloadProgress(index, timelapses.Count, timelapse, bytes, finishedBefore + bytes, batchSize)));
            var result = await DownloadOneAsync(timelapse, folder, fileProgress, cancellationToken);
            results.Add(result);
            done += timelapse.Size;
            progress?.Report(new DownloadProgress(index, timelapses.Count, timelapse, timelapse.Size, done, batchSize));
        }

        return results;
    }

    /// <summary>Where a timelapse goes in <paramref name="folder"/>. Refuses names that could leave the folder.</summary>
    public static string TargetPath(string folder, Timelapse timelapse)
    {
        if (!TimelapseNames.TryParseVideo(timelapse.Name, out _)
            || timelapse.Name.Contains('/') || timelapse.Name.Contains('\\') || timelapse.Name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Refusing to save '{timelapse.Name}': unexpected file name.", nameof(timelapse));
        }

        return Path.Combine(folder, timelapse.Name);
    }

    private async Task<DownloadResult> DownloadOneAsync(Timelapse timelapse, string folder, IProgress<long> progress, CancellationToken cancellationToken)
    {
        string target;
        try
        {
            target = TargetPath(folder, timelapse);
        }
        catch (ArgumentException e)
        {
            return new DownloadResult(timelapse, DownloadOutcome.Failed, null, Error: e.Message);
        }

        if (File.Exists(target))
        {
            return new FileInfo(target).Length == timelapse.Size
                ? new DownloadResult(timelapse, DownloadOutcome.SkippedExisting, target)
                : new DownloadResult(timelapse, DownloadOutcome.SkippedConflict, target,
                    Error: "A different file with this name is already in the folder.");
        }

        if (cache is not null && cache.HasVideo(timelapse))
        {
            File.Copy(cache.VideoPath(timelapse), target);
            progress.Report(timelapse.Size);
            return new DownloadResult(timelapse, DownloadOutcome.CopiedFromCache, target);
        }

        var partial = target + PartialSuffix;
        long resumedFrom = 0;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await session.RunAsync(client =>
                {
                    // Read the partial file's length on every attempt (including the session's own reconnect).
                    var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                    if (have > timelapse.Size)
                    {
                        have = 0; // not ours or the remote file changed: start again
                    }

                    resumedFrom = have;
                    return have == timelapse.Size
                        ? Task.CompletedTask
                        : client.DownloadAsync(timelapse.RemotePath, partial, have, progress, cancellationToken);
                }, cancellationToken);

                var length = new FileInfo(partial).Length;
                if (length != timelapse.Size)
                {
                    throw new IOException($"The download ended early ({length:N0} of {timelapse.Size:N0} bytes).");
                }

                File.Move(partial, target);
                return new DownloadResult(timelapse, DownloadOutcome.Downloaded, target, resumedFrom, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new DownloadResult(timelapse, DownloadOutcome.Cancelled, null, resumedFrom, attempt);
            }
            catch (IOException e) when (e is not (FtpReplyException or PrinterAuthenticationException or PrinterCertificateMismatchException)
                                        && attempt < MaxAttempts)
            {
                // Dropped connection: wait, then continue from the partial file.
                try
                {
                    await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return new DownloadResult(timelapse, DownloadOutcome.Cancelled, null, resumedFrom, attempt);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new DownloadResult(timelapse, DownloadOutcome.Failed, null, resumedFrom, attempt, e.Message);
            }
        }
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
