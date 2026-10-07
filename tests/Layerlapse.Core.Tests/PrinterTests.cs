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

    [PrinterFact]
    public async Task Exact_modification_time_matches_curl()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();

        // curl -I ftps://<printer-ip>:990/timelapse/video_2025-07-07_07-17-11.mp4 -> Last-Modified: Mon, 07 Jul 2025 06:57:46 GMT
        var time = await client.GetModifiedTimeAsync("/timelapse/video_2025-07-07_07-17-11.mp4");

        Assert.Equal(new DateTime(2025, 7, 7, 6, 57, 46, DateTimeKind.Utc), time);
        Assert.Equal(DateTimeKind.Utc, time!.Value.Kind);
    }

    [PrinterFact]
    public async Task Missing_file_is_an_ftp_reply_and_the_connection_survives()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        var target = Path.Combine(Path.GetTempPath(), $"layerlapse-missing-{Guid.NewGuid():N}.jpg");

        var error = await Assert.ThrowsAsync<FtpReplyException>(() => client.DownloadAsync("/timelapse/thumbnail/does-not-exist.jpg", target));
        Assert.Equal(550, error.ReplyCode);
        Assert.NotEmpty(await client.ListAsync("/timelapse/"));
        File.Delete(target);
    }

    [PrinterFact]
    public async Task Ranged_reads_match_the_whole_file_and_keep_the_connection_usable()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();
        const string path = "/timelapse/video_2026-09-15_16-12-12.mp4"; // 5,424,735 bytes
        var whole = Path.Combine(Path.GetTempPath(), $"layerlapse-range-{Guid.NewGuid():N}.mp4");
        try
        {
            await client.DownloadAsync(path, whole);
            var expected = await File.ReadAllBytesAsync(whole);

            foreach (var (offset, length) in new[] { (0L, 100), (1_000_000L, 65536), (expected.Length - 5000L, 65536), (2_500_000L, 1) })
            {
                var range = await client.ReadRangeAsync(path, offset, length);
                Assert.Equal(expected.AsSpan((int)offset, Math.Min(length, expected.Length - (int)offset)).ToArray(), range);
            }

            Assert.NotEmpty(await client.ListAsync("/timelapse/")); // the control connection is still fine
        }
        finally
        {
            File.Delete(whole);
        }
    }

    /// <summary>
    /// The only write-command check against the real printer: DELE on a well-formed timelapse name that does not
    /// exist. It cannot remove anything, and shows the printer answers 550 and the connection survives.
    /// </summary>
    [PrinterFact]
    public async Task Deleting_a_timelapse_that_does_not_exist_is_a_550()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();

        var error = await Assert.ThrowsAsync<FtpReplyException>(() => client.DeleteAsync("/timelapse/video_1999-01-01_00-00-00.mp4"));

        Assert.Equal(550, error.ReplyCode);
        Assert.NotEmpty(await client.ListAsync("/timelapse/"));
    }

    [PrinterFact]
    public async Task Exposes_serial_from_certificate()
    {
        await using var client = CreateClient();
        await client.ConnectAsync();

        Assert.False(string.IsNullOrWhiteSpace(client.Serial));
        output.WriteLine($"Serial prefix: {client.Serial![..3]}");
    }

    [PrinterFact]
    public async Task Wrong_access_code_gives_a_clear_error()
    {
        // One failed login per run; vsftpd drops the connection after repeated failures.
        await using var client = new BambuFtpsClient(new PrinterConnection(PrinterEnvironment.Host!, "00000000"));

        var error = await Assert.ThrowsAsync<PrinterAuthenticationException>(() => client.ConnectAsync());
        Assert.Contains("access code", error.Message);
        output.WriteLine(error.Message);
    }

    [PrinterFact]
    public async Task Nothing_listening_gives_unreachable_error()
    {
        await using var client = new BambuFtpsClient(new PrinterConnection(PrinterEnvironment.Host!, "00000000", FtpsPort: 9));

        await Assert.ThrowsAsync<PrinterUnreachableException>(() => client.ConnectAsync());
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
