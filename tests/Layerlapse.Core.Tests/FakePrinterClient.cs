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

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteFolder, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RemoteEntry>>([new RemoteEntry("timelapse", "/timelapse", 0, DateTime.Now, true)]);

        public Task DownloadAsync(string remotePath, string localPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
