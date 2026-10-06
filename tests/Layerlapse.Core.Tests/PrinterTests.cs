using System.Security.Cryptography;
using Layerlapse.Core.Printers;
using Xunit.Abstractions;

namespace Layerlapse.Core.Tests;

/// <summary>Read-only tests against the real printer. Skipped unless LAYERLAPSE_IP and LAYERLAPSE_CODE are set.</summary>
[Trait("Category", "Printer")]
public class PrinterTests(ITestOutputHelper output)
{
    private static BambuFtpsClient CreateClient() =>
        new(new PrinterConnection(PrinterEnvironment.Host!, PrinterEnvironment.AccessCode!));

    [PrinterFact]
    public async Task Lists_timelapse_folder()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();

        var entries = await client.ListAsync("/timelapse/");

        Assert.Contains(entries, e => e.IsDirectory && e.Name == "thumbnail");
        Assert.Contains(entries, e => !e.IsDirectory && e.Name.StartsWith("video_", StringComparison.Ordinal) && e.Name.EndsWith(".mp4", StringComparison.Ordinal));
        output.WriteLine($"{entries.Count} entries in /timelapse/");
    }

    [PrinterFact]
    public async Task Lists_root_with_unicode_names()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();

        var entries = await client.ListAsync("/");

        Assert.Contains(entries, e => e.IsDirectory && e.Name == "timelapse");
        Assert.DoesNotContain(entries, e => e.Name.Contains('�'));
        output.WriteLine($"{entries.Count} entries in /, {entries.Count(e => e.Name.Any(c => c > 127))} with non-ASCII names");
    }

    [PrinterFact]
    public async Task Downloads_smallest_timelapse_completely()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        var smallest = (await client.ListAsync("/timelapse/"))
            .Where(e => !e.IsDirectory && e.Name.EndsWith(".mp4", StringComparison.Ordinal))
            .MinBy(e => e.Size)!;
        var localPath = Path.Combine(Path.GetTempPath(), $"layerlapse-test-{Guid.NewGuid():N}.mp4");

        try
        {
            long lastProgress = 0;
            await client.DownloadAsync(smallest.FullPath, localPath, new SyncProgress(b => lastProgress = b));

            var bytes = await File.ReadAllBytesAsync(localPath);
            Assert.Equal(smallest.Size, bytes.Length);
            Assert.Equal(smallest.Size, lastProgress);
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));
            output.WriteLine($"{smallest.Name}: {bytes.Length} bytes, SHA-256 {Convert.ToHexString(SHA256.HashData(bytes))}");
        }
        finally
        {
            File.Delete(localPath);
        }
    }

    [PrinterFact]
    public async Task Rejects_a_wrong_pinned_certificate()
    {
        await using var client = new BambuFtpsClient(new PrinterConnection(
            PrinterEnvironment.Host!, PrinterEnvironment.AccessCode!, PinnedFingerprint: new string('0', 64)));

        var error = await Assert.ThrowsAsync<PrinterCertificateMismatchException>(() => client.ConnectAsync());
        Assert.Equal(64, error.ActualFingerprint.Length);
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
