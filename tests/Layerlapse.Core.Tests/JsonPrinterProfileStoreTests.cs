using Layerlapse.Core.Setup;

namespace Layerlapse.Core.Tests;

public sealed class JsonPrinterProfileStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "layerlapse-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private JsonPrinterProfileStore Store() => new(Path.Combine(_folder, "printers.json"));

    private static PrinterProfile Profile(string id, string host = "192.168.1.50") =>
        new(id, id, host, new string('C', 64), new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Empty_when_no_file()
    {
        Assert.Null(await Store().GetLastAsync());
    }

    [Fact]
    public async Task Round_trips_and_tracks_last_used()
    {
        await Store().SaveAsync(Profile("A"));
        await Store().SaveAsync(Profile("B"));
        await Store().SaveAsync(Profile("A", "192.168.1.51"));

        var last = await Store().GetLastAsync();
        Assert.Equal(Profile("A", "192.168.1.51"), last);
    }

    [Fact]
    public async Task Removing_last_falls_back_to_another()
    {
        await Store().SaveAsync(Profile("A"));
        await Store().SaveAsync(Profile("B"));

        await Store().RemoveAsync("B");

        Assert.Equal("A", (await Store().GetLastAsync())!.Id);
    }

    [Fact]
    public async Task File_holds_no_secret_fields()
    {
        await Store().SaveAsync(Profile("A"));

        var json = await File.ReadAllTextAsync(Path.Combine(_folder, "printers.json"));
        Assert.DoesNotContain("code", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pinnedFingerprint", json);
    }

    [Fact]
    public async Task Corrupt_file_reads_as_empty()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "printers.json"), "{ not json");

        Assert.Null(await Store().GetLastAsync());
    }
}
