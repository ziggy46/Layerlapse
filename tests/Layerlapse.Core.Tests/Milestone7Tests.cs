using System.Net;
using System.Text;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.Core.Tests;

public class DeletePolicyTests
{
    [Theory]
    [InlineData("/timelapse/video_2026-10-04_09-44-43.mp4")]
    [InlineData("/timelapse/thumbnail/video_2026-10-04_09-44-43.jpg")]
    public void Allows_only_timelapses_and_their_thumbnails(string path) => Assert.True(DeletePolicy.IsAllowed(path));

    [Theory]
    [InlineData("/verify_job")]
    [InlineData("/certificate/printer.cer")]
    [InlineData("/Under_Desk_Cable_Clip.gcode.3mf")]
    [InlineData("/timelapse/")]
    [InlineData("/timelapse")]
    [InlineData("/ipcam/ipcam-record.2026-10-04_19-27-11.0.mp4")]
    [InlineData("/timelapse/../verify_job")]
    [InlineData("/timelapse/video_2026-10-04_09-44-43.mp4/../../verify_job")]
    [InlineData("timelapse/video_2026-10-04_09-44-43.mp4")]
    [InlineData("/timelapse/video_2026-10-04_09-44-43.mp4 ")]
    [InlineData("/timelapse/video_2026-10-04_09-44-43.mp4\r\nDELE /verify_job")]
    [InlineData("/timelapse/video_2026-10-04_09-44-43.mp4\n")]
    [InlineData("/timelapse/thumbnail/video_2026-10-04_09-44-43.jpg\n")]
    [InlineData("/timelapse/thumbnail/video_2026-10-04_09-44-43.mp4")]
    public void Refuses_everything_else(string path)
    {
        Assert.False(DeletePolicy.IsAllowed(path));
        Assert.Throws<UnauthorizedAccessException>(() => DeletePolicy.Check(path));
    }
}

public sealed class TimelapseDeleteTests : IDisposable
{
    private const string Name = "video_2026-10-04_09-44-43.mp4";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "layerlapse-del-" + Guid.NewGuid().ToString("N"));
    private readonly FakePrinter _printer = new();

    public TimelapseDeleteTests()
    {
        _printer.AddFile("/timelapse/" + Name, 3000, new DateTime(2026, 10, 4, 23, 44, 43, DateTimeKind.Utc));
        _printer.AddFile("/timelapse/thumbnail/video_2026-10-04_09-44-43.jpg", 50, new DateTime(2026, 10, 4, 23, 44, 0, DateTimeKind.Utc));
        _printer.AddFile("/timelapse/video_2026-10-03_17-47-45.mp4", 800, new DateTime(2026, 10, 3, 23, 7, 8, DateTimeKind.Utc));
        _printer.AddFile("/verify_job", 16, DateTime.UtcNow);
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
    public async Task Deletes_the_video_then_its_thumbnail_and_updates_the_cache()
    {
        var library = Library();
        var target = (await library.RefreshAsync()).Timelapses.Single(t => t.Name == Name);
        var cachedVideo = library.Cache.VideoPath(target);
        await File.WriteAllBytesAsync(cachedVideo, _printer.Files[target.RemotePath].Data);

        await library.DeleteAsync(target, deletingEnabled: true);

        Assert.Equal(["/timelapse/" + Name, "/timelapse/thumbnail/video_2026-10-04_09-44-43.jpg"], _printer.Deletes);
        Assert.DoesNotContain("/timelapse/" + Name, _printer.Files.Keys);
        Assert.Contains("/verify_job", _printer.Files.Keys);
        Assert.Equal(["video_2026-10-03_17-47-45.mp4"], (await library.LoadCachedAsync()).Timelapses.Select(t => t.Name));
        Assert.True(File.Exists(cachedVideo)); // kept: it may now be the only copy
    }

    [Fact]
    public async Task Sends_nothing_when_deleting_is_turned_off()
    {
        var library = Library();
        var target = (await library.RefreshAsync()).Timelapses[0];

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.DeleteAsync(target, deletingEnabled: false));

        Assert.Empty(_printer.Deletes);
    }

    [Fact]
    public async Task Missing_thumbnail_is_not_an_error()
    {
        _printer.Files.Remove("/timelapse/thumbnail/video_2026-10-04_09-44-43.jpg");
        var library = Library();
        var target = (await library.RefreshAsync()).Timelapses.Single(t => t.Name == Name);

        await library.DeleteAsync(target, deletingEnabled: true);

        Assert.DoesNotContain("/timelapse/" + Name, _printer.Files.Keys);
    }

    [Fact]
    public async Task Client_refuses_disallowed_paths_before_sending()
    {
        await using var session = new PrinterSession(new PrinterConnection("192.168.1.50", _printer.AccessCode), _printer.Create);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.RunAsync(c => c.DeleteAsync("/verify_job")));

        Assert.Empty(_printer.Deletes);
        Assert.Contains("/verify_job", _printer.Files.Keys);
    }
}

public sealed class StorageUsageTests
{
    [Fact]
    public async Task Adds_up_space_used_per_kind_of_file()
    {
        var printer = new FakePrinter();
        printer.AddFile("/timelapse/video_2026-10-04_09-44-43.mp4", 3000, DateTime.UtcNow);
        printer.AddFile("/timelapse/video_2026-10-03_17-47-45.mp4", 1000, DateTime.UtcNow);
        printer.AddFile("/timelapse/thumbnail/video_2026-10-04_09-44-43.jpg", 50, DateTime.UtcNow);
        printer.AddFile("/ipcam/ipcam-record.2026-10-04_19-27-11.0.mp4", 9000, DateTime.UtcNow);
        printer.AddFile("/Clip.gcode.3mf", 700, DateTime.UtcNow);
        printer.AddFile("/verify_job", 16, DateTime.UtcNow);
        await using var session = new PrinterSession(new PrinterConnection("192.168.1.50", printer.AccessCode), printer.Create);

        var usage = await StorageUsage.MeasureAsync(session);

        Assert.Equal(new StorageCategory(2, 4000), usage.Timelapses);
        Assert.Equal(new StorageCategory(1, 50), usage.Thumbnails);
        Assert.Equal(new StorageCategory(1, 9000), usage.CameraRecordings);
        Assert.Equal(new StorageCategory(1, 700), usage.Models);
        Assert.Equal(new StorageCategory(1, 16), usage.Other);
        Assert.Equal(13766, usage.TotalBytes);
    }
}

public class AutoDownloadTests
{
    [Fact]
    public void Only_timelapses_finished_after_enabling_are_new()
    {
        Timelapse T(string name, DateTime modified) => new(name, 1, DateTime.Now, modified, null);
        var since = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
        var old = T("video_2026-10-03_17-47-45.mp4", new DateTime(2026, 10, 3, 23, 7, 8, DateTimeKind.Utc));
        var newer = T("video_2026-10-04_09-44-43.mp4", new DateTime(2026, 10, 4, 23, 44, 43, DateTimeKind.Utc));

        Assert.Equal([newer], AutoDownload.SelectNew([newer, old], since));
    }
}

public sealed class MultiplePrintersTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "layerlapse-multi-" + Guid.NewGuid().ToString("N"), "printers.json");

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_file)))
        {
            Directory.Delete(Path.GetDirectoryName(_file)!, recursive: true);
        }
    }

    [Fact]
    public async Task Keeps_all_printers_in_order_and_switches_the_last_used()
    {
        var store = new JsonPrinterProfileStore(_file);
        PrinterProfile P(string id) => new(id, id, "192.168.1.50", new string('C', 64), DateTimeOffset.UtcNow);
        await store.SaveAsync(P("A"));
        await store.SaveAsync(P("B"));
        await store.SaveAsync(P("A") with { Host = "192.168.1.51" });

        Assert.Equal(["A", "B"], (await store.GetAllAsync()).Select(p => p.Id));
        Assert.Equal("192.168.1.51", (await store.GetLastAsync())!.Host);

        await store.SetLastAsync("B");
        Assert.Equal("B", (await store.GetLastAsync())!.Id);

        await store.SetLastAsync("missing");
        Assert.Equal("B", (await store.GetLastAsync())!.Id);
    }
}

public class UpdateCheckerTests
{
    private sealed class Feed(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static readonly Uri FeedUri = new("https://example.invalid/releases/latest");

    [Fact]
    public async Task Reports_a_newer_release()
    {
        var checker = new UpdateChecker(new HttpClient(new Feed("""{"tag_name":"v99.1.0","html_url":"https://example.invalid/r/99.1.0"}""")), FeedUri);

        var update = await checker.CheckAsync();

        Assert.Equal(new Version(99, 1, 0), update!.Version);
        Assert.Equal("https://example.invalid/r/99.1.0", update.Page.ToString());
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.0.1","html_url":"https://example.invalid/r"}""", HttpStatusCode.OK)]
    [InlineData("""not json""", HttpStatusCode.OK)]
    [InlineData("""{}""", HttpStatusCode.InternalServerError)]
    public async Task Quietly_reports_nothing_when_up_to_date_or_unreadable(string json, HttpStatusCode status)
    {
        Assert.Null(await new UpdateChecker(new HttpClient(new Feed(json, status)), FeedUri).CheckAsync());
    }

    [Fact]
    public async Task Does_nothing_without_a_feed()
    {
        var checker = new UpdateChecker(new HttpClient(new Feed("{}")), null);

        Assert.False(checker.IsConfigured);
        Assert.Null(await checker.CheckAsync());
    }

    [Fact]
    public void Knows_its_own_version() => Assert.Equal(new Version(0, 8, 2), UpdateChecker.CurrentVersion);
}

public sealed class SettingsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "layerlapse-set-" + Guid.NewGuid().ToString("N"), "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_file)))
        {
            Directory.Delete(Path.GetDirectoryName(_file)!, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_is_off_by_default_and_new_fields_round_trip()
    {
        var store = new JsonSettingsStore(_file);
        var defaults = await store.LoadAsync();
        Assert.False(defaults.AllowDelete);
        Assert.False(defaults.AutoDownloadEnabled);

        var since = new DateTime(2026, 10, 7, 1, 0, 0, DateTimeKind.Utc);
        await store.SaveAsync(defaults with { AllowDelete = true, AutoDownloadFolder = "/x", AutoDownloadSince = since });

        var loaded = await new JsonSettingsStore(_file).LoadAsync();
        Assert.True(loaded.AllowDelete);
        Assert.True(loaded.AutoDownloadEnabled);
        Assert.Equal(since, loaded.AutoDownloadSince);
        Assert.DoesNotContain("autoDownloadEnabled", await File.ReadAllTextAsync(_file));
    }
}
