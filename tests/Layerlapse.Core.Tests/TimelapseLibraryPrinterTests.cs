using System.Diagnostics;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Timelapses;
using Xunit.Abstractions;

namespace Layerlapse.Core.Tests;

/// <summary>The timelapse library against the real printer. Read-only; downloads thumbnails and one small video.</summary>
[Trait("Category", "Printer")]
public sealed class TimelapseLibraryPrinterTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layerlapse-real-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TimelapseLibrary Library(PrinterSession session) => new(session, new TimelapseCache(_root));

    private static PrinterSession Session() =>
        new(new PrinterConnection(PrinterEnvironment.Host!, PrinterEnvironment.AccessCode!), c => new BambuFtpsClient(c));

    [PrinterFact]
    public async Task Cold_and_warm_timings()
    {
        var clock = Stopwatch.StartNew();
        TimelapseListing cold;
        await using (var session = Session())
        {
            cold = await Library(session).RefreshAsync();
            var listed = clock.Elapsed;

            foreach (var timelapse in cold.Timelapses)
            {
                await Library(session).GetThumbnailAsync(timelapse);
            }

            output.WriteLine($"Cold: {cold.Timelapses.Count} timelapses listed (with MDTM) in {listed.TotalSeconds:F1} s, thumbnails in {(clock.Elapsed - listed).TotalSeconds:F1} s more");
            output.WriteLine($"Clock offset: {cold.Clock!.PrinterToUtc} from {cold.Clock.Source}");
            output.WriteLine($"Durations known for {cold.Timelapses.Count(t => t.ApproximateDuration is not null)} of {cold.Timelapses.Count}");
        }

        clock.Restart();
        await using (var session = Session())
        {
            var warm = await Library(session).LoadCachedAsync();
            var shown = clock.Elapsed;
            var refreshed = await Library(session).RefreshAsync();
            output.WriteLine($"Warm: {warm.Timelapses.Count} shown from cache in {shown.TotalMilliseconds:F0} ms, refreshed in {clock.Elapsed.TotalSeconds:F1} s");

            Assert.Equal(cold.Timelapses.Count, warm.Timelapses.Count);
            Assert.Equal(cold.Timelapses, refreshed.Timelapses);
            Assert.True(shown < TimeSpan.FromSeconds(1));
        }
    }

    [PrinterFact]
    public async Task Video_in_cache_is_complete()
    {
        await using var session = Session();
        var library = Library(session);
        var video = (await library.RefreshAsync()).Timelapses.Single(t => t.Name == "video_2026-10-03_17-47-45.mp4");

        var path = await library.GetVideoAsync(video);

        Assert.Equal(video.Size, new FileInfo(path).Length);
        output.WriteLine($"{video.Name}: {video.Size:N0} bytes, ≈ {video.ApproximateDuration}");
    }
}
