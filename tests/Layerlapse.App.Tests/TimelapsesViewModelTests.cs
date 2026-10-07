using Layerlapse.App.ViewModels;
using Layerlapse.Core.Credentials;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;
using Layerlapse.Core.Tests;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.App.Tests;

internal sealed class FakePlayer : IVideoPlayer
{
    public List<string> Played { get; } = [];

    public void Play(string localPath) => Played.Add(localPath);
}

public sealed class TimelapsesViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layerlapse-grid-" + Guid.NewGuid().ToString("N"));
    private readonly FakePrinter _printer = new();
    private readonly FakePlayer _player = new();

    public TimelapsesViewModelTests()
    {
        _printer.AddFile("/timelapse/video_2026-10-03_17-47-45.mp4", 800, new DateTime(2026, 10, 3, 23, 7, 8, DateTimeKind.Utc));
        _printer.AddFile("/timelapse/video_2026-10-04_09-44-43.mp4", 3000, new DateTime(2026, 10, 4, 23, 44, 43, DateTimeKind.Utc));
        _printer.AddFile("/timelapse/video_2025-07-07_07-17-11.mp4", 1500, new DateTime(2025, 7, 7, 6, 57, 46, DateTimeKind.Utc));
        _printer.AddFile("/ipcam/ipcam-record.2026-10-04_19-27-11.0.mp4", 10, new DateTime(2026, 10, 5, 0, 32, 30, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TimelapsesViewModel Grid() => new("00M000000000001", new TimelapseCache(_root), _player);

    private PrinterSession Session() => new(new PrinterConnection("192.168.1.50", _printer.AccessCode), _printer.Create);

    [Fact]
    public async Task Lists_newest_first_with_size_and_approximate_duration()
    {
        await using var grid = Grid();
        await grid.AttachAsync(Session());

        Assert.Equal(3, grid.Items.Count);
        Assert.Equal("3 timelapses · durations (≈) are approximate", grid.Status);
        var newest = grid.Items[0];
        Assert.Equal(new DateTime(2026, 10, 4, 9, 44, 43), newest.Timelapse.Start);
        Assert.Equal("≈ 9 h 00 min", newest.DurationText);
        Assert.Equal("Duration unknown", grid.Items[2].DurationText); // modified before its start time
        Assert.Equal("800 bytes", grid.Items[1].SizeText);
    }

    [Fact]
    public async Task Second_launch_shows_the_cache_before_connecting()
    {
        await using (var first = Grid())
        {
            await first.AttachAsync(Session());
        }

        _printer.Reachable = false;
        await using var second = Grid();
        await second.LoadCachedAsync();

        Assert.Equal(3, second.Items.Count);
        Assert.Contains("updating", second.Status);
        Assert.False(second.IsConnected);
    }

    [Fact]
    public async Task Filters_by_date_range()
    {
        await using var grid = Grid();
        await grid.AttachAsync(Session());

        grid.From = new DateTime(2026, 10, 1);
        Assert.Equal(2, grid.Items.Count);

        grid.To = new DateTime(2026, 10, 3);
        Assert.Equal("video_2026-10-03_17-47-45.mp4", Assert.Single(grid.Items).Timelapse.Name);

        grid.From = new DateTime(2020, 1, 1);
        grid.To = new DateTime(2020, 1, 2);
        Assert.True(grid.IsFilteredEmpty);
        Assert.False(grid.IsEmpty);

        grid.ClearFilterCommand.Execute(null);
        Assert.Equal(3, grid.Items.Count);
    }

    [Fact]
    public async Task Play_downloads_to_the_cache_then_opens_the_player()
    {
        await using var grid = Grid();
        await grid.AttachAsync(Session());
        var item = grid.Items[1];

        await grid.PlayCommand.ExecuteAsync(item);
        await grid.PlayCommand.ExecuteAsync(item);

        Assert.Equal(2, _player.Played.Count);
        Assert.Equal(_printer.Files[item.Timelapse.RemotePath].Data, await File.ReadAllBytesAsync(_player.Played[0]));
        Assert.Equal(1, _printer.Downloads - ThumbnailDownloads());
        Assert.False(item.IsDownloading);
        Assert.Null(item.Error);
    }

    [Fact]
    public async Task Play_without_a_connection_explains_why()
    {
        await using (var first = Grid())
        {
            await first.AttachAsync(Session());
        }

        await using var offline = Grid();
        await offline.LoadCachedAsync();
        await offline.PlayCommand.ExecuteAsync(offline.Items[0]);

        Assert.Contains("Connect to the printer", offline.Items[0].Error);
        Assert.Empty(_player.Played);
    }

    [Fact]
    public async Task Download_failure_shows_on_the_card()
    {
        await using var grid = Grid();
        await grid.AttachAsync(Session());
        _printer.NextDownloadFailure = new FtpReplyException("RETR", 550, "550 Failed to open file.");

        await grid.PlayCommand.ExecuteAsync(grid.Items[0]);

        Assert.Contains("550", grid.Items[0].Error);
        Assert.Empty(_player.Played);
    }

    [Fact]
    public async Task Empty_folder_shows_the_empty_state()
    {
        _printer.Files.Clear();
        await using var grid = Grid();

        await grid.AttachAsync(Session());

        Assert.True(grid.IsEmpty);
        Assert.Empty(grid.Items);
    }

    private int ThumbnailDownloads() => 0; // the fake printer has no thumbnails, so every download is a video
}

public sealed class MainViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "layerlapse-main-" + Guid.NewGuid().ToString("N"));
    private readonly FakePrinter _printer = new();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly JsonPrinterProfileStore _profiles;

    public MainViewModelTests()
    {
        _profiles = new JsonPrinterProfileStore(Path.Combine(_folder, "printers.json"));
        _printer.AddFile("/timelapse/video_2026-10-04_09-44-43.mp4", 3000, new DateTime(2026, 10, 4, 23, 44, 43, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Background thumbnail work may still be finishing; the temp folder is disposable.
        }
    }

    private MainViewModel Launch()
    {
        var setup = new PrinterSetupService(_credentials, _profiles, _printer.Create);
        return new MainViewModel(new ConnectionViewModel(setup), setup, id => new TimelapseCache(Path.Combine(_folder, "cache", id)), new FakePlayer());
    }

    [Fact]
    public async Task First_setup_moves_to_the_timelapses()
    {
        var main = Launch();
        await main.InitializeAsync();
        Assert.Equal(AppPage.Printer, main.CurrentPage);

        main.Connection.Host = "192.168.1.50";
        main.Connection.AccessCode = _printer.AccessCode;
        await main.Connection.SaveCommand.ExecuteAsync(null);
        await WaitUntil(() => main.Timelapses?.Items.Count == 1);

        Assert.Equal(AppPage.Timelapses, main.CurrentPage);
        Assert.Same(main.Timelapses, main.CurrentContent);
    }

    [Fact]
    public async Task Relaunch_opens_on_the_timelapses_and_the_printer_page_is_one_click_away()
    {
        var first = Launch();
        await first.InitializeAsync();
        first.Connection.Host = "192.168.1.50";
        first.Connection.AccessCode = _printer.AccessCode;
        await first.Connection.SaveCommand.ExecuteAsync(null);
        await WaitUntil(() => first.Timelapses?.Items.Count == 1);

        var main = Launch();
        await main.InitializeAsync();
        await WaitUntil(() => main.Timelapses?.IsConnected == true);

        Assert.Equal(AppPage.Timelapses, main.CurrentPage);
        Assert.Single(main.Timelapses!.Items);

        main.ShowPrinterCommand.Execute(null);
        Assert.Same(main.Connection, main.CurrentContent);
        main.NavIndex = 0;
        Assert.Same(main.Timelapses, main.CurrentContent);
    }

    [Fact]
    public async Task Forgetting_the_printer_closes_its_timelapses()
    {
        var main = Launch();
        await main.InitializeAsync();
        main.Connection.Host = "192.168.1.50";
        main.Connection.AccessCode = _printer.AccessCode;
        await main.Connection.SaveCommand.ExecuteAsync(null);
        await WaitUntil(() => main.Timelapses is not null);

        await main.Connection.ForgetCommand.ExecuteAsync(null);

        Assert.Null(main.Timelapses);
        Assert.Equal(AppPage.Printer, main.CurrentPage);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
