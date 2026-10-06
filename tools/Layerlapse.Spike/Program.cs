// Milestone 1 spike: connect over implicit FTPS, list /timelapse/, download the smallest video.
// Read-only. Reads LAYERLAPSE_IP and LAYERLAPSE_CODE; never prints the code.
// Usage: dotnet run --project tools/Layerlapse.Spike -- [--library bc|fluentftp] <output-folder>
//   bc (default): BambuFtpsClient, BouncyCastle TLS with session resumption.
//   fluentftp:    FtpsPrinterClient, kept to reproduce the 522 "session reuse required" failure.
using System.Diagnostics;
using System.Security.Cryptography;
using Layerlapse.Core.Printers;

var host = Environment.GetEnvironmentVariable("LAYERLAPSE_IP");
var code = Environment.GetEnvironmentVariable("LAYERLAPSE_CODE");
if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(code))
{
    Console.Error.WriteLine("Set LAYERLAPSE_IP and LAYERLAPSE_CODE.");
    return 2;
}

var library = "bc";
var rest = new List<string>(args);
var flag = rest.IndexOf("--library");
if (flag >= 0 && flag + 1 < rest.Count)
{
    library = rest[flag + 1];
    rest.RemoveRange(flag, 2);
}

var outDir = rest.Count > 0 ? rest[0] : Path.Combine(Path.GetTempPath(), "layerlapse-spike");
Directory.CreateDirectory(outDir);

var connection = new PrinterConnection(host, code);
await using IPrinterClient client = library switch
{
    "fluentftp" => new FtpsPrinterClient(connection),
    "bc" => new BambuFtpsClient(connection),
    _ => throw new ArgumentException($"Unknown library '{library}'."),
};
Console.WriteLine($"Library: {library}");

var sw = Stopwatch.StartNew();
await client.ConnectAsync();
Console.WriteLine($"Connected in {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"Certificate SHA-256: {client.CertificateFingerprint}");
if (client is BambuFtpsClient bambu)
{
    Console.WriteLine($"Greeting: {bambu.Greeting}");
}

foreach (var folder in new[] { "/timelapse/thumbnail/", "/ipcam/" })
{
    var entries = await client.ListAsync(folder);
    Console.WriteLine($"{folder}: {entries.Count} entries");
    foreach (var e in entries.Take(5))
    {
        Console.WriteLine($"  {(e.IsDirectory ? "d" : "-")} {e.Size,10} {e.Modified:yyyy-MM-dd HH:mm} {e.Name}");
    }
}

sw.Restart();
var listing = await client.ListAsync("/timelapse/");
var videos = listing.Where(e => !e.IsDirectory && e.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToList();
Console.WriteLine($"/timelapse/: {listing.Count} entries, {videos.Count} videos, listed in {sw.ElapsedMilliseconds} ms");
foreach (var e in videos.OrderByDescending(v => v.Modified).Take(3))
{
    Console.WriteLine($"  {e.Size,10} {e.Modified:yyyy-MM-dd HH:mm} {e.Name}");
}

var smallest = videos.MinBy(v => v.Size) ?? throw new InvalidOperationException("No videos found.");
var localPath = Path.Combine(outDir, smallest.Name);
sw.Restart();
await client.DownloadAsync(smallest.FullPath, localPath);
var bytes = await File.ReadAllBytesAsync(localPath);
Console.WriteLine($"Downloaded {smallest.FullPath} ({bytes.Length} of {smallest.Size} bytes) in {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"SHA-256: {Convert.ToHexString(SHA256.HashData(bytes))}");
Console.WriteLine($"Saved to {localPath}");
return bytes.Length == smallest.Size ? 0 : 1;
