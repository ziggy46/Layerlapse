namespace Layerlapse.Core.Printers;

/// <summary>
/// Read-only access to a printer's storage. Nothing here writes to or deletes from the printer.
/// </summary>
public interface IPrinterClient : IAsyncDisposable
{
    /// <summary>SHA-256 fingerprint of the printer's TLS certificate, known after a successful connect.</summary>
    string? CertificateFingerprint { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteFolder, CancellationToken cancellationToken = default);

    /// <summary>Downloads one file, overwriting <paramref name="localPath"/>. Progress reports bytes transferred.</summary>
    Task DownloadAsync(
        string remotePath,
        string localPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}
