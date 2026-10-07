using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.Core.Tests;

public sealed class TimelapseDownloaderTests : IDisposable
{
    private const string Name = "video_2026-10-04_09-44-43.mp4";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layerlapse-dl-" + Guid.NewGuid().ToString("N"));
    private readonly FakePrinter _printer = new();
    private readonly List<TimeSpan> _waits = [];

    public TimelapseDownloaderTests()
    {
        _printer.AddFile("/timelapse/" + Name, 5000, new DateTime(2026, 10, 4, 23, 44, 43, DateTimeKind.Utc));
        _printer.AddFile("/timelapse/video_2026-10-03_17-47-45.mp4", 800, new DateTime(2026, 10, 3, 23, 7, 8, DateTimeKind.Utc));
    }

    private string Folder => Path.Combine(_root, "Downloads");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Timelapse Video(string name, long size) =>
        new(name, size, TimelapseNames.TryParseVideo(name, out var s) ? s : default, DateTime.UtcNow, null);

    private static readonly Timelapse Big = Video(Name, 5000);
    private static readonly Timelapse Small = Video("video_2026-10-03_17-47-45.mp4", 800);

    private TimelapseDownloader Downloader(TimelapseCache? cache = null) =>
        new(new PrinterSession(new PrinterConnection("192.168.1.50", _printer.AccessCode), _printer.Create), cache,
            (wait, _) => { _waits.Add(wait); return Task.CompletedTask; });

    private byte[] Remote(Timelapse t) => _printer.Files[t.RemotePath].Data;

    [Fact]
    public async Task Downloads_several_files_and_reports_progress()
    {
        var reports = new List<DownloadProgress>();

        var results = await Downloader().DownloadAsync([Big, Small], Folder, new SyncProgress<DownloadProgress>(reports.Add));

        Assert.All(results, r => Assert.Equal(DownloadOutcome.Downloaded, r.Outcome));
        Assert.Equal(Remote(Big), await File.ReadAllBytesAsync(Path.Combine(Folder, Big.Name)));
        Assert.Equal(Remote(Small), await File.ReadAllBytesAsync(Path.Combine(Folder, Small.Name)));
        Assert.Equal(5800, reports[^1].BatchBytes);
        Assert.Equal(5800, reports[^1].BatchSize);
        Assert.Empty(Directory.GetFiles(Folder, "*.part"));
    }

    [Fact]
    public async Task Skips_files_already_in_the_folder()
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Path.Combine(Folder, Big.Name), Remote(Big));
        await File.WriteAllBytesAsync(Path.Combine(Folder, Small.Name), new byte[3]);

        var results = await Downloader().DownloadAsync([Big, Small], Folder);

        Assert.Equal(DownloadOutcome.SkippedExisting, results[0].Outcome);
        Assert.Equal(DownloadOutcome.SkippedConflict, results[1].Outcome);
        Assert.Equal(3, new FileInfo(Path.Combine(Folder, Small.Name)).Length); // never overwritten
        Assert.Equal(0, _printer.Downloads);
    }

    [Fact]
    public async Task Dropped_connection_resumes_instead_of_restarting()
    {
        _printer.DropsAtByte.Enqueue(2000);
        _printer.DropsAtByte.Enqueue(3500);

        var result = (await Downloader().DownloadAsync([Big], Folder))[0];

        Assert.Equal(DownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal(Remote(Big), await File.ReadAllBytesAsync(result.Path!));
        Assert.Equal([0L, 2000L, 3500L], _printer.RestartOffsets);
        Assert.Equal(5000, _printer.BytesSent); // every byte sent once
    }

    [Fact]
    public async Task Cancelled_download_keeps_its_part_and_continues_next_time()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            if (p.FileBytes >= 2500)
            {
                cancel.Cancel();
            }
        });

        var first = (await Downloader().DownloadAsync([Big, Small], Folder, progress, cancel.Token));
        Assert.Equal(DownloadOutcome.Cancelled, first[0].Outcome);
        Assert.Equal(DownloadOutcome.Cancelled, first[1].Outcome);
        var kept = new FileInfo(Path.Combine(Folder, Big.Name + ".part")).Length;
        Assert.InRange(kept, 2500, 4999);
        Assert.False(File.Exists(Path.Combine(Folder, Big.Name)));

        var second = (await Downloader().DownloadAsync([Big], Folder))[0];

        Assert.Equal(DownloadOutcome.Downloaded, second.Outcome);
        Assert.Equal(kept, second.ResumedFrom);
        Assert.Equal(Remote(Big), await File.ReadAllBytesAsync(second.Path!));
    }

    [Fact]
    public async Task Gives_up_after_repeated_drops_and_keeps_the_part()
    {
        for (var i = 0; i < 20; i++)
        {
            _printer.DropsAtByte.Enqueue(100 * (i + 1));
        }

        var result = (await Downloader().DownloadAsync([Big], Folder))[0];

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Equal(TimelapseDownloader.MaxAttempts, result.Attempts);
        Assert.Equal([1, 2, 4, 8], _waits.Select(w => w.TotalSeconds));
        Assert.True(File.Exists(Path.Combine(Folder, Big.Name + ".part")));
    }

    [Fact]
    public async Task Missing_file_fails_without_retrying()
    {
        var missing = Video("video_2026-01-01_00-00-00.mp4", 10);

        var result = (await Downloader().DownloadAsync([missing], Folder))[0];

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("550", result.Error);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Played_videos_are_copied_from_the_cache()
    {
        var cache = new TimelapseCache(Path.Combine(_root, "cache"));
        await File.WriteAllBytesAsync(cache.VideoPath(Small), Remote(Small));

        var result = (await Downloader(cache).DownloadAsync([Small], Folder))[0];

        Assert.Equal(DownloadOutcome.CopiedFromCache, result.Outcome);
        Assert.Equal(0, _printer.Downloads);
    }

    [Fact]
    public async Task Too_large_partial_starts_again()
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Path.Combine(Folder, Small.Name + ".part"), new byte[900]);

        var result = (await Downloader().DownloadAsync([Small], Folder))[0];

        Assert.Equal(DownloadOutcome.Downloaded, result.Outcome);
        Assert.Equal(0, result.ResumedFrom);
        Assert.Equal(Remote(Small), await File.ReadAllBytesAsync(result.Path!));
    }

    [Theory]
    [InlineData("../video_2026-10-04_09-44-43.mp4")]
    [InlineData("notes.txt")]
    public void Refuses_names_that_could_leave_the_folder(string name)
    {
        Assert.Throws<ArgumentException>(() => TimelapseDownloader.TargetPath("/tmp/x", Video(name, 1)));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "layerlapse-settings-" + Guid.NewGuid().ToString("N"), "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_file)))
        {
            Directory.Delete(Path.GetDirectoryName(_file)!, recursive: true);
        }
    }

    [Fact]
    public async Task Round_trips_and_defaults_when_missing()
    {
        var store = new JsonSettingsStore(_file);
        Assert.Null((await store.LoadAsync()).LastDownloadFolder);

        await store.SaveAsync(new AppSettings("/Users/me/Timelapses"));

        Assert.Equal("/Users/me/Timelapses", (await new JsonSettingsStore(_file).LoadAsync()).LastDownloadFolder);
    }
}
