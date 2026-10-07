using Layerlapse.Core.Credentials;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.Core.Tests;

public class TimelapseNamesTests
{
    [Fact]
    public void Parses_video_start_time()
    {
        Assert.True(TimelapseNames.TryParseVideo("video_2026-10-04_09-44-43.mp4", out var start));
        Assert.Equal(new DateTime(2026, 10, 4, 9, 44, 43), start);
        Assert.Equal("video_2026-10-04_09-44-43.jpg", TimelapseNames.ThumbnailName("video_2026-10-04_09-44-43.mp4"));
    }

    [Theory]
    [InlineData("video_2026-10-04_09-44-43.avi")]
    [InlineData("../video_2026-10-04_09-44-43.mp4")]
    [InlineData("video_2026-13-04_09-44-43.mp4")]
    [InlineData("thumbnail")]
    [InlineData("video_2026-10-04_09-44-43.mp4\r\nDELE x")]
    public void Rejects_anything_else(string name) => Assert.False(TimelapseNames.TryParseVideo(name, out _));
}

public class PrinterClockTests
{
    private static RemoteEntry Recording(string name, DateTime modifiedUtc) => new(name, "/ipcam/" + name, 251_000_000, modifiedUtc, false);

    [Fact]
    public void Measures_offset_from_camera_recordings()
    {
        // Real values from 2026-10-04: names on the printer clock, listing times in UTC, ~5 minute segments.
        var recordings = new[]
        {
            Recording("ipcam-record.2026-10-04_19-27-11.0.mp4", new DateTime(2026, 10, 5, 0, 32, 0, DateTimeKind.Utc)),
            Recording("ipcam-record.2026-10-04_19-32-16.1.mp4", new DateTime(2026, 10, 5, 0, 37, 0, DateTimeKind.Utc)),
            Recording("ipcam-record.2026-10-04_19-37-22.2.mp4", new DateTime(2026, 10, 5, 0, 41, 0, DateTimeKind.Utc)),
            Recording("index", new DateTime(2026, 10, 5, 0, 37, 0, DateTimeKind.Utc)),
        };

        var clock = PrinterClockOffset.Estimate(recordings, DateTime.Now);

        Assert.Equal(TimeSpan.FromHours(5), clock.PrinterToUtc);
        Assert.Equal(ClockOffsetSource.CameraRecordings, clock.Source);
    }

    [Fact]
    public void Falls_back_to_this_computers_zone()
    {
        var clock = PrinterClockOffset.Estimate([], new DateTime(2026, 10, 4, 12, 0, 0));

        Assert.Equal(ClockOffsetSource.ComputerTimeZone, clock.Source);
        Assert.Equal(-TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 4, 12, 0, 0)), clock.PrinterToUtc);
    }

    [Fact]
    public void Duration_corrects_for_the_printer_clock()
    {
        var clock = new PrinterClockOffset(TimeSpan.FromHours(5), ClockOffsetSource.CameraRecordings);

        // video_2026-10-03_17-47-45.mp4, modified 2026-10-03 23:07:08 UTC: about 19 minutes.
        var duration = Timelapse.Duration(new DateTime(2026, 10, 3, 17, 47, 45), new DateTime(2026, 10, 3, 23, 7, 8, DateTimeKind.Utc), clock);

        Assert.Equal(new TimeSpan(0, 19, 23), duration);
    }

    [Theory]
    [InlineData(2025, 7, 7, 7, 17, 11, 2025, 7, 7, 6, 57, 46)] // modified before it started: clock was different
    [InlineData(2026, 9, 19, 20, 58, 18, 2026, 9, 22, 21, 25, 34)] // more than two days
    public void Implausible_durations_are_unknown(int y, int mo, int d, int h, int mi, int s, int my, int mmo, int md, int mh, int mmi, int ms)
    {
        var clock = new PrinterClockOffset(TimeSpan.FromHours(5), ClockOffsetSource.CameraRecordings);

        Assert.Null(Timelapse.Duration(new DateTime(y, mo, d, h, mi, s), new DateTime(my, mmo, md, mh, mmi, ms, DateTimeKind.Utc), clock));
    }
}

public sealed class TimelapseCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layerlapse-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Timelapse Video(string name, long size = 10) => new(name, size, DateTime.Now, DateTime.UtcNow, null);

    [Fact]
    public void Evicts_least_recently_used_videos_over_the_cap()
    {
        var cache = new TimelapseCache(_root, videoCapBytes: 25);
        var a = Video("video_2026-01-01_00-00-01.mp4");
        var b = Video("video_2026-01-01_00-00-02.mp4");
        var c = Video("video_2026-01-01_00-00-03.mp4");
        foreach (var (video, minutesAgo) in new[] { (a, 30), (b, 20), (c, 10) })
        {
            File.WriteAllBytes(cache.VideoPath(video), new byte[10]);
            File.SetLastWriteTimeUtc(cache.VideoPath(video), DateTime.UtcNow.AddMinutes(-minutesAgo));
        }

        cache.EvictVideos(keep: cache.VideoPath(a));

        Assert.True(File.Exists(cache.VideoPath(a)));
        Assert.False(File.Exists(cache.VideoPath(b)));
        Assert.True(File.Exists(cache.VideoPath(c)));
    }

    [Fact]
    public void Only_complete_videos_count()
    {
        var cache = new TimelapseCache(_root);
        var video = Video("video_2026-01-01_00-00-01.mp4", size: 10);
        File.WriteAllBytes(cache.VideoPath(video), new byte[4]);

        Assert.False(cache.HasVideo(video));
    }

    [Fact]
    public void Removes_leftover_partial_downloads()
    {
        Directory.CreateDirectory(Path.Combine(_root, "videos"));
        var partial = Path.Combine(_root, "videos", "video_2026-01-01_00-00-01.mp4.part");
        File.WriteAllBytes(partial, new byte[3]);

        _ = new TimelapseCache(_root);

        Assert.False(File.Exists(partial));
    }

    [Fact]
    public void Refuses_unsafe_names()
    {
        var cache = new TimelapseCache(_root);

        Assert.Throws<ArgumentException>(() => cache.VideoPath(Video("../escape.mp4")));
    }

    [Fact]
    public void Folder_names_are_safe()
    {
        Assert.Equal("host_192.168.1.50", TimelapseCache.SafeFolderName("host:192.168.1.50"));
    }
}

public sealed class TimelapseLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layerlapse-lib-" + Guid.NewGuid().ToString("N"));
    private readonly FakePrinter _printer = new();

    public TimelapseLibraryTests()
    {
        _printer.AddFile("/timelapse/video_2026-10-03_17-47-45.mp4", 800, new DateTime(2026, 10, 3, 23, 7, 8, DateTimeKind.Utc));
        _printer.AddFile("/timelapse/video_2026-10-04_09-44-43.mp4", 3000, new DateTime(2026, 10, 4, 23, 44, 43, DateTimeKind.Utc));
        _printer.AddFile("/timelapse/thumbnail/video_2026-10-04_09-44-43.jpg", 50, new DateTime(2026, 10, 4, 23, 44, 0, DateTimeKind.Utc));
        _printer.AddFile("/timelapse/notes.txt", 5, DateTime.UtcNow);
        _printer.AddFile("/ipcam/ipcam-record.2026-10-04_19-27-11.0.mp4", 10, new DateTime(2026, 10, 5, 0, 32, 30, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TimelapseLibrary Library() =>
        new(new PrinterSession(new PrinterConnection("192.168.1.50", _printer.AccessCode), _printer.Create), new TimelapseCache(_root));

    [Fact]
    public async Task Refresh_lists_newest_first_with_exact_times_and_durations()
    {
        var listing = await Library().RefreshAsync();

        Assert.Equal(["video_2026-10-04_09-44-43.mp4", "video_2026-10-03_17-47-45.mp4"], listing.Timelapses.Select(t => t.Name));
        Assert.Equal(TimeSpan.FromHours(5), listing.Clock!.PrinterToUtc);
        var newest = listing.Timelapses[0];
        Assert.Equal(new DateTime(2026, 10, 4, 23, 44, 43, DateTimeKind.Utc), newest.ModifiedUtc); // seconds from MDTM
        Assert.Equal(TimeSpan.FromHours(9), newest.ApproximateDuration);
    }

    [Fact]
    public async Task Second_launch_shows_the_cached_listing_without_the_printer()
    {
        await Library().RefreshAsync();
        _printer.Reachable = false;

        var cached = await Library().LoadCachedAsync();

        Assert.Equal(2, cached.Timelapses.Count);
        Assert.Equal(TimeSpan.FromHours(9), cached.Timelapses[0].ApproximateDuration);
    }

    [Fact]
    public async Task Refresh_asks_exact_times_only_for_new_files()
    {
        await Library().RefreshAsync();
        Assert.Equal(2, _printer.ModifiedTimeRequests);

        _printer.AddFile("/timelapse/video_2026-10-05_08-00-00.mp4", 900, new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc));
        var listing = await Library().RefreshAsync();

        Assert.Equal(3, _printer.ModifiedTimeRequests);
        Assert.Equal(3, listing.Timelapses.Count);
    }

    [Fact]
    public async Task Thumbnails_are_cached_and_missing_ones_are_null()
    {
        var library = Library();
        var listing = await library.RefreshAsync();

        var path = await library.GetThumbnailAsync(listing.Timelapses[0]);
        Assert.Equal(50, new FileInfo(path!).Length);
        var downloads = _printer.Downloads;
        Assert.Equal(path, await library.GetThumbnailAsync(listing.Timelapses[0]));
        Assert.Equal(downloads, _printer.Downloads);

        Assert.Null(await library.GetThumbnailAsync(listing.Timelapses[1]));
    }

    [Fact]
    public async Task Video_downloads_once_then_plays_from_cache()
    {
        var library = Library();
        var video = (await library.RefreshAsync()).Timelapses[1];
        long reported = 0;

        var path = await library.GetVideoAsync(video, new SyncProgress(b => reported = b));
        var again = await library.GetVideoAsync(video);

        Assert.Equal(path, again);
        Assert.Equal(_printer.Files[video.RemotePath].Data, await File.ReadAllBytesAsync(path));
        Assert.Equal(800, reported);
        Assert.Equal(1, _printer.Downloads);
    }

    [Fact]
    public async Task Failed_download_leaves_nothing_behind()
    {
        var library = Library();
        var video = (await library.RefreshAsync()).Timelapses[1];
        _printer.NextDownloadFailure = new IOException("The printer closed the connection.");
        _printer.NextDownloadFailure = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => library.GetVideoAsync(video));

        Assert.False(library.Cache.HasVideo(video));
        Assert.Empty(Directory.GetFiles(_root, "*.part", SearchOption.AllDirectories));
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

public sealed class PrinterSessionTests
{
    [Fact]
    public async Task Reconnects_once_after_a_dropped_connection()
    {
        var printer = new FakePrinter();
        printer.AddFile("/timelapse/video_2026-10-03_17-47-45.mp4", 10, DateTime.UtcNow);
        await using var session = new PrinterSession(new PrinterConnection("192.168.1.50", printer.AccessCode), printer.Create);
        var calls = 0;

        var result = await session.RunAsync(async client =>
        {
            if (calls++ == 0)
            {
                throw new IOException("The printer closed the connection.");
            }

            return await client.ListAsync("/timelapse/");
        });

        Assert.Single(result);
        Assert.Equal(2, printer.Connections.Count);
    }

    [Fact]
    public async Task Does_not_retry_an_unreachable_printer()
    {
        var printer = new FakePrinter { Reachable = false };
        await using var session = new PrinterSession(new PrinterConnection("192.168.1.50", printer.AccessCode), printer.Create);

        await Assert.ThrowsAsync<PrinterUnreachableException>(() => session.RunAsync(c => c.ListAsync("/")));
        Assert.Single(printer.Connections);
    }

    [Fact]
    public async Task Does_not_retry_a_wrong_code()
    {
        var printer = new FakePrinter();
        await using var session = new PrinterSession(new PrinterConnection("192.168.1.50", "00000000"), printer.Create);

        await Assert.ThrowsAsync<PrinterAuthenticationException>(() => session.RunAsync(c => c.ListAsync("/")));
        Assert.Single(printer.Connections);
    }

    [Fact]
    public async Task Runs_operations_one_at_a_time()
    {
        var printer = new FakePrinter();
        await using var session = new PrinterSession(new PrinterConnection("192.168.1.50", printer.AccessCode), printer.Create);
        var active = 0;
        var maxActive = 0;

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => session.RunAsync(async _ =>
        {
            maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
        })));

        Assert.Equal(1, maxActive);
        Assert.Single(printer.Connections);
    }
}

public sealed class DefaultAppVideoPlayerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"layerlapse-player-{Guid.NewGuid():N}.mp4");

    public DefaultAppVideoPlayerTests() => File.WriteAllBytes(_file, [0]);

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void Handing_the_file_to_an_already_running_app_is_not_an_error()
    {
        // Windows: Process.Start returns null for shell launches that reuse an app (reported on 0.7.0).
        var player = new Layerlapse.Core.Timelapses.DefaultAppVideoPlayer(_ => null);

        player.Play(_file);
    }

    [Fact]
    public void No_app_for_the_file_is_a_clear_error()
    {
        var player = new Layerlapse.Core.Timelapses.DefaultAppVideoPlayer(_ => throw new System.ComponentModel.Win32Exception(1155));

        var error = Assert.Throws<InvalidOperationException>(() => player.Play(_file));
        Assert.Contains("No app is set up", error.Message);
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        Assert.Throws<FileNotFoundException>(() => new Layerlapse.Core.Timelapses.DefaultAppVideoPlayer(_ => null).Play(_file + ".missing"));
    }
}
