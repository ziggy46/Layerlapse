using System.Diagnostics;
using System.Security.Cryptography;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Timelapses;
using Xunit.Abstractions;

namespace Layerlapse.Core.Tests;

/// <summary>
/// Milestone 5 acceptance against the real printer: an interrupted ~57 MB download resumes instead of
/// restarting. Opt-in twice over (printer variables plus LAYERLAPSE_HEAVY_TESTS=1) because it moves ~57 MB.
/// </summary>
[Trait("Category", "Printer")]
public sealed class TimelapseDownloaderPrinterTests(ITestOutputHelper output) : IDisposable
{
    private const string Largest = "video_2026-09-12_22-39-03.mp4";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "layerlapse-real-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class HeavyFactAttribute : FactAttribute
    {
        public HeavyFactAttribute()
        {
            if (!PrinterEnvironment.IsConfigured || Environment.GetEnvironmentVariable("LAYERLAPSE_HEAVY_TESTS") != "1")
            {
                Skip = "Set the printer variables and LAYERLAPSE_HEAVY_TESTS=1 to run the 57 MB resume test.";
            }
        }
    }

    [HeavyFact]
    public async Task Interrupted_large_download_resumes_instead_of_restarting()
    {
        var connection = new PrinterConnection(PrinterEnvironment.Host!, PrinterEnvironment.AccessCode!);
        long transferred = 0;
        long lastReported = 0;
        var dropAt = 20L * 1024 * 1024;
        var dropped = false;

        // A client whose connection breaks once, abruptly, after 20 MB of the file.
        IPrinterClient Create(PrinterConnection c) => new BreakingClient(new BambuFtpsClient(c), bytes =>
        {
            transferred += Math.Max(0, bytes - lastReported);
            lastReported = bytes;
            if (!dropped && bytes >= dropAt)
            {
                dropped = true;
                throw new IOException("Simulated dropped connection at " + bytes.ToString("N0") + " bytes.");
            }
        }, offset => lastReported = offset);

        Timelapse video;
        await using (var session = new PrinterSession(connection, Create))
        {
            video = (await new TimelapseLibrary(session, new TimelapseCache(Path.Combine(_folder, "cache"))).RefreshAsync())
                .Timelapses.Single(t => t.Name == Largest);
        }

        // 1. Dropped connection: the downloader reconnects and continues by itself.
        var clock = Stopwatch.StartNew();
        DownloadResult first;
        await using (var session = new PrinterSession(connection, Create))
        {
            first = (await new TimelapseDownloader(session).DownloadAsync([video], Path.Combine(_folder, "a")))[0];
        }

        output.WriteLine($"Dropped run: {first.Outcome}, last attempt resumed from {first.ResumedFrom:N0} bytes, " +
                         $"{transferred:N0} bytes transferred for a {video.Size:N0} byte file, {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(DownloadOutcome.Downloaded, first.Outcome);
        Assert.True(first.ResumedFrom >= dropAt, "should have continued from the partial file");
        Assert.InRange(transferred, video.Size, video.Size + (2 * 1024 * 1024)); // no restart from zero
        Assert.Equal(video.Size, new FileInfo(first.Path!).Length);

        // 2. Cancelled by the user part-way, then started again later: continues from the .part file.
        transferred = 0;
        lastReported = 0;
        dropped = true; // no simulated drop this time
        using var cancel = new CancellationTokenSource();
        var folder = Path.Combine(_folder, "b");
        await using (var session = new PrinterSession(connection, Create))
        {
            var progress = new SyncProgress(p =>
            {
                if (p.FileBytes >= 35L * 1024 * 1024)
                {
                    cancel.Cancel();
                }
            });
            var cancelled = (await new TimelapseDownloader(session).DownloadAsync([video], folder, progress, cancel.Token))[0];
            Assert.Equal(DownloadOutcome.Cancelled, cancelled.Outcome);
        }

        var kept = new FileInfo(Path.Combine(folder, Largest + TimelapseDownloader.PartialSuffix)).Length;
        DownloadResult resumed;
        await using (var session = new PrinterSession(connection, Create))
        {
            resumed = (await new TimelapseDownloader(session).DownloadAsync([video], folder))[0];
        }

        output.WriteLine($"Cancel + restart: kept {kept:N0} bytes, resumed from {resumed.ResumedFrom:N0}, {resumed.Outcome}");
        Assert.Equal(kept, resumed.ResumedFrom);
        Assert.Equal(DownloadOutcome.Downloaded, resumed.Outcome);

        var a = SHA256.HashData(await File.ReadAllBytesAsync(first.Path!));
        var b = SHA256.HashData(await File.ReadAllBytesAsync(resumed.Path!));
        output.WriteLine($"SHA-256 (dropped run):  {Convert.ToHexString(a)}");
        output.WriteLine($"SHA-256 (cancel+resume): {Convert.ToHexString(b)}");
        Assert.Equal(a, b);
    }

    /// <summary>Passes everything through, but lets the test observe progress and break the transfer.</summary>
    private sealed class BreakingClient(IPrinterClient inner, Action<long> onProgress, Action<long> onStart) : IPrinterClient
    {
        public string? CertificateFingerprint => inner.CertificateFingerprint;

        public string? Serial => inner.Serial;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteFolder, CancellationToken cancellationToken = default) =>
            inner.ListAsync(remoteFolder, cancellationToken);

        public Task<DateTime?> GetModifiedTimeAsync(string remotePath, CancellationToken cancellationToken = default) =>
            inner.GetModifiedTimeAsync(remotePath, cancellationToken);

        public Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
            DownloadAsync(remotePath, localPath, 0, progress, cancellationToken);

        public Task DownloadAsync(string remotePath, string localPath, long resumeFrom, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            onStart(resumeFrom);
            return inner.DownloadAsync(remotePath, localPath, resumeFrom, new Relay(b => { onProgress(b); progress?.Report(b); }), cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private sealed class Relay(Action<long> report) : IProgress<long>
        {
            public void Report(long value) => report(value);
        }
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
