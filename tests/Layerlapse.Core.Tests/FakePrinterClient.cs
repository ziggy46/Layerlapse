using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Tests;

/// <summary>A scripted printer for setup tests. Behaves like BambuFtpsClient's pinning and login checks.</summary>
internal sealed class FakePrinter
{
    public string AccessCode { get; set; } = "12345678";

    public string Fingerprint { get; set; } = new('A', 64);

    public string? Serial { get; set; } = "00M000000000001";

    public bool Reachable { get; set; } = true;

    /// <summary>Addresses where nothing answers (to simulate the printer moving).</summary>
    public HashSet<string> UnreachableHosts { get; } = [];

    /// <summary>Thrown after the TLS handshake, like a dropped connection or an unexpected FTP reply.</summary>
    public Exception? ConnectFailure { get; set; }

    public List<PrinterConnection> Connections { get; } = [];

    /// <summary>Remote files by full path ("/timelapse/video_….mp4").</summary>
    public Dictionary<string, FakeFile> Files { get; } = new(StringComparer.Ordinal);

    public int Downloads { get; private set; }

    public int ModifiedTimeRequests { get; private set; }

    /// <summary>Thrown by the next download, then cleared.</summary>
    public Exception? NextDownloadFailure { get; set; }

    /// <summary>Each value drops one transfer after that many bytes of the file (a lost connection).</summary>
    public Queue<long> DropsAtByte { get; } = new();

    /// <summary>Restart offsets requested, in order (0 for a fresh download).</summary>
    public List<long> RestartOffsets { get; } = [];

    public int RangeReads { get; private set; }

    /// <summary>Every DELE the fake received, in order.</summary>
    public List<string> Deletes { get; } = [];

    /// <summary>Bytes actually sent over all transfers.</summary>
    public long BytesSent { get; private set; }

    public void AddFile(string path, int size, DateTime modifiedUtc) =>
        Files[path] = new FakeFile(Enumerable.Range(0, size).Select(i => (byte)(i * 7)).ToArray(), modifiedUtc);

    public IPrinterClient Create(PrinterConnection connection)
    {
        Connections.Add(connection);
        return new Client(this, connection);
    }

    private sealed class Client(FakePrinter printer, PrinterConnection connection) : IPrinterClient
    {
        public string? CertificateFingerprint { get; private set; }

        public string? Serial { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (!printer.Reachable || printer.UnreachableHosts.Contains(connection.Host))
            {
                throw new PrinterUnreachableException(connection.Host);
            }

            CertificateFingerprint = printer.Fingerprint;
            Serial = printer.Serial;
            if (printer.ConnectFailure is { } failure)
            {
                throw failure;
            }

            if (connection.PinnedFingerprint is not null && connection.PinnedFingerprint != printer.Fingerprint)
            {
                throw new PrinterCertificateMismatchException(connection.PinnedFingerprint, printer.Fingerprint);
            }

            if (connection.AccessCode != printer.AccessCode)
            {
                throw new PrinterAuthenticationException();
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteFolder, CancellationToken cancellationToken = default)
        {
            var prefix = remoteFolder.EndsWith('/') ? remoteFolder : remoteFolder + "/";
            // Like vsftpd: minute precision in listings.
            var entries = printer.Files
                .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && !f.Key[prefix.Length..].Contains('/'))
                .Select(f => new RemoteEntry(f.Key[prefix.Length..], f.Key, f.Value.Data.Length, TruncateToMinute(f.Value.ModifiedUtc), false))
                .ToList();
            if (prefix == "/")
            {
                entries.Insert(0, new RemoteEntry("timelapse", "/timelapse", 0, DateTime.UtcNow, true));
            }

            return Task.FromResult<IReadOnlyList<RemoteEntry>>(entries);
        }

        public Task<DateTime?> GetModifiedTimeAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            printer.ModifiedTimeRequests++;
            return Task.FromResult(printer.Files.TryGetValue(remotePath, out var file) ? file.ModifiedUtc : (DateTime?)null);
        }

        public Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
            DownloadAsync(remotePath, localPath, 0, progress, cancellationToken);

        public Task DeleteAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            DeletePolicy.Check(remotePath);
            printer.Deletes.Add(remotePath);
            if (!printer.Files.Remove(remotePath))
            {
                throw new FtpReplyException("DELE", 550, "550 Delete operation failed.");
            }

            return Task.CompletedTask;
        }

        public Task<byte[]> ReadRangeAsync(string remotePath, long offset, int length, CancellationToken cancellationToken = default)
        {
            if (!printer.Files.TryGetValue(remotePath, out var file))
            {
                throw new FtpReplyException("RETR", 550, "550 Failed to open file.");
            }

            printer.RangeReads++;
            var start = (int)Math.Min(offset, file.Data.Length);
            var count = Math.Min(length, file.Data.Length - start);
            printer.BytesSent += count;
            return Task.FromResult(file.Data.AsSpan(start, count).ToArray());
        }

        public async Task DownloadAsync(string remotePath, string localPath, long resumeFrom, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            if (printer.NextDownloadFailure is { } failure)
            {
                printer.NextDownloadFailure = null;
                throw failure;
            }

            if (!printer.Files.TryGetValue(remotePath, out var file))
            {
                throw new FtpReplyException("RETR", 550, "550 Failed to open file.");
            }

            printer.Downloads++;
            printer.RestartOffsets.Add(resumeFrom);
            await using var stream = new FileStream(localPath, resumeFrom > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write);
            stream.SetLength(resumeFrom);
            stream.Seek(resumeFrom, SeekOrigin.Begin);
            var end = printer.DropsAtByte.Count > 0 ? Math.Min(printer.DropsAtByte.Dequeue(), file.Data.Length) : file.Data.Length;
            for (var position = resumeFrom; position < file.Data.Length; position += 100)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = (int)Math.Min(100, file.Data.Length - position);
                if (position + chunk > end)
                {
                    await stream.WriteAsync(file.Data.AsMemory((int)position, (int)(end - position)), cancellationToken);
                    printer.BytesSent += end - position;
                    throw new IOException("The printer closed the connection.");
                }

                await stream.WriteAsync(file.Data.AsMemory((int)position, chunk), cancellationToken);
                printer.BytesSent += chunk;
                progress?.Report(position + chunk);
            }
        }

        private static DateTime TruncateToMinute(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed record FakeFile(byte[] Data, DateTime ModifiedUtc);
