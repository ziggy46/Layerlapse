using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Timelapses;

/// <param name="ResumedFrom">Bytes that were already on disk when the last attempt started (0 = from scratch).</param>
/// <param name="Attempts">Transfer attempts, including retries after dropped connections.</param>
public sealed record DownloadResult(Timelapse Timelapse, DownloadOutcome Outcome, string? Path, long ResumedFrom = 0, int Attempts = 0, string? Error = null);

/// <param name="Index">Position of the current file in the batch, from 0.</param>
public sealed record DownloadProgress(int Index, int Count, Timelapse Current, long FileBytes, long BatchBytes, long BatchSize);

/// <summary>
/// Downloads timelapses with <see cref="PrinterFileDownloader"/>: only well-formed timelapse names are saved,
/// and a video already in the play cache is copied instead of downloaded.
/// </summary>
public sealed class TimelapseDownloader(PrinterSession session, TimelapseCache? cache = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string PartialSuffix = PrinterFileDownloader.PartialSuffix;

    public const int MaxAttempts = PrinterFileDownloader.MaxAttempts;

    private readonly PrinterFileDownloader _downloader = new(session, delay);

    public async Task<IReadOnlyList<DownloadResult>> DownloadAsync(
        IReadOnlyList<Timelapse> timelapses,
        string folder,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new DownloadResult?[timelapses.Count];
        var items = new List<DownloadItem>();
        var indexOf = new List<int>();
        for (var i = 0; i < timelapses.Count; i++)
        {
            var timelapse = timelapses[i];
            try
            {
                var name = Path.GetFileName(TargetPath(folder, timelapse));
                items.Add(new DownloadItem(timelapse.RemotePath, name, timelapse.Size,
                    cache is not null && cache.HasVideo(timelapse) ? cache.VideoPath(timelapse) : null));
                indexOf.Add(i);
            }
            catch (ArgumentException e)
            {
                results[i] = new DownloadResult(timelapse, DownloadOutcome.Failed, null, Error: e.Message);
            }
        }

        var relay = progress is null ? null : new Relay(p => progress.Report(
            new DownloadProgress(indexOf[p.Index], timelapses.Count, timelapses[indexOf[p.Index]], p.FileBytes, p.BatchBytes, p.BatchSize)));
        var fileResults = await _downloader.DownloadAsync(items, folder, relay, cancellationToken);
        for (var k = 0; k < fileResults.Count; k++)
        {
            var r = fileResults[k];
            results[indexOf[k]] = new DownloadResult(timelapses[indexOf[k]], r.Outcome, r.Path, r.ResumedFrom, r.Attempts, r.Error);
        }

        return results.Select(r => r!).ToList();
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

    private sealed class Relay(Action<FileDownloadProgress> report) : IProgress<FileDownloadProgress>
    {
        public void Report(FileDownloadProgress value) => report(value);
    }
}
