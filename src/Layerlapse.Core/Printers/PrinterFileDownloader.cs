using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Printers;

public enum DownloadOutcome
{
    Downloaded,

    /// <summary>Already in the destination with the same size; not downloaded again.</summary>
    SkippedExisting,

    /// <summary>A different file with the same name is in the destination; left untouched.</summary>
    SkippedConflict,

    /// <summary>Copied from a local cache, so nothing was transferred.</summary>
    CopiedFromCache,

    Failed,

    /// <summary>Stopped by the user. The partial file is kept, and the next download continues from it.</summary>
    Cancelled,
}

/// <summary>One file to fetch.</summary>
/// <param name="FileName">Name to save as. Must already be safe (see <see cref="PrinterFileDownloader.SafeFileName"/>).</param>
/// <param name="CachedCopy">A complete local copy to use instead of downloading, if any.</param>
public sealed record DownloadItem(string RemotePath, string FileName, long Size, string? CachedCopy = null);

/// <param name="ResumedFrom">Bytes that were already on disk when the last attempt started (0 = from scratch).</param>
/// <param name="Attempts">Transfer attempts, including retries after dropped connections.</param>
public sealed record FileDownloadResult(DownloadItem Item, DownloadOutcome Outcome, string? Path, long ResumedFrom = 0, int Attempts = 0, string? Error = null);

/// <param name="Index">Position of the current file in the batch, from 0.</param>
public sealed record FileDownloadProgress(int Index, int Count, DownloadItem Current, long FileBytes, long BatchBytes, long BatchSize);

/// <summary>
/// Downloads printer files into a folder the user chose, one at a time. Files already there are skipped. Each
/// file is written to "&lt;name&gt;.part" and renamed when complete, so a cancelled or dropped download
/// continues from where it stopped (FTP REST) instead of starting again. Read-only towards the printer.
/// </summary>
public sealed class PrinterFileDownloader(PrinterSession session, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string PartialSuffix = ".part";

    /// <summary>Attempts per file. Waits 1, 2, 4 and 8 seconds between them.</summary>
    public const int MaxAttempts = 5;

    private static readonly char[] Unsafe = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>
    /// A file name that is valid on macOS, Windows and Linux: path separators and characters Windows forbids
    /// become "_", control characters are removed. Returns null for names that cannot be made safe.
    /// </summary>
    public static string? SafeFileName(string remoteName)
    {
        var chars = remoteName.Where(c => !char.IsControl(c)).Select(c => Array.IndexOf(Unsafe, c) >= 0 ? '_' : c).ToArray();
        var name = new string(chars).Trim();
        return name.Length == 0 || name.Trim('.').Length == 0 ? null : name;
    }

    public async Task<IReadOnlyList<FileDownloadResult>> DownloadAsync(
        IReadOnlyList<DownloadItem> items,
        string folder,
        IProgress<FileDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var batchSize = items.Sum(t => t.Size);
        long done = 0;
        var results = new List<FileDownloadResult>();

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new FileDownloadResult(item, DownloadOutcome.Cancelled, null));
                continue;
            }

            var finishedBefore = done;
            var fileProgress = new SyncProgress(bytes => progress?.Report(new FileDownloadProgress(index, items.Count, item, bytes, finishedBefore + bytes, batchSize)));
            var result = await DownloadOneAsync(item, folder, fileProgress, cancellationToken);
            results.Add(result);
            done += item.Size;
            progress?.Report(new FileDownloadProgress(index, items.Count, item, item.Size, done, batchSize));
        }

        return results;
    }

    private async Task<FileDownloadResult> DownloadOneAsync(DownloadItem item, string folder, IProgress<long> progress, CancellationToken cancellationToken)
    {
        if (SafeFileName(item.FileName) != item.FileName)
        {
            return new FileDownloadResult(item, DownloadOutcome.Failed, null, Error: $"Refusing to save '{item.FileName}': unsafe file name.");
        }

        var target = Path.Combine(folder, item.FileName);
        if (File.Exists(target))
        {
            return new FileInfo(target).Length == item.Size
                ? new FileDownloadResult(item, DownloadOutcome.SkippedExisting, target)
                : new FileDownloadResult(item, DownloadOutcome.SkippedConflict, target,
                    Error: "A different file with this name is already in the folder.");
        }

        if (item.CachedCopy is { } cached && new FileInfo(cached) is { Exists: true } cachedFile && cachedFile.Length == item.Size)
        {
            File.Copy(cached, target);
            progress.Report(item.Size);
            return new FileDownloadResult(item, DownloadOutcome.CopiedFromCache, target);
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
                    if (have > item.Size)
                    {
                        have = 0; // not ours or the remote file changed: start again
                    }

                    resumedFrom = have;
                    return have == item.Size
                        ? Task.CompletedTask
                        : client.DownloadAsync(item.RemotePath, partial, have, progress, cancellationToken);
                }, cancellationToken);

                var length = new FileInfo(partial).Length;
                if (length != item.Size)
                {
                    throw new IOException($"The download ended early ({length:N0} of {item.Size:N0} bytes).");
                }

                File.Move(partial, target);
                return new FileDownloadResult(item, DownloadOutcome.Downloaded, target, resumedFrom, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new FileDownloadResult(item, DownloadOutcome.Cancelled, null, resumedFrom, attempt);
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
                    return new FileDownloadResult(item, DownloadOutcome.Cancelled, null, resumedFrom, attempt);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new FileDownloadResult(item, DownloadOutcome.Failed, null, resumedFrom, attempt, e.Message);
            }
        }
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
