namespace Layerlapse.Core.Printers;

/// <summary>
/// Read-only access to a printer's storage. Nothing here writes to or deletes from the printer.
/// </summary>
public interface IPrinterClient : IAsyncDisposable
{
    /// <summary>SHA-256 fingerprint of the printer's TLS certificate, known after a successful connect.</summary>
    string? CertificateFingerprint { get; }

    /// <summary>The printer's serial number (its certificate's common name), known after the TLS handshake.</summary>
    string? Serial { get; }

    /// <summary>Connects and logs in.</summary>
    /// <exception cref="PrinterUnreachableException">Nothing answered at the address.</exception>
    /// <exception cref="PrinterAuthenticationException">The access code was rejected.</exception>
    /// <exception cref="PrinterCertificateMismatchException">The certificate does not match the pinned one.</exception>
    /// <exception cref="PrinterConnectionException">Any other connection failure.</exception>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteFolder, CancellationToken cancellationToken = default);

    /// <summary>Exact modification time in UTC (FTP MDTM), or null if the printer does not say.</summary>
    Task<DateTime?> GetModifiedTimeAsync(string remotePath, CancellationToken cancellationToken = default);

    /// <summary>Downloads one file, overwriting <paramref name="localPath"/>. Progress reports bytes transferred.</summary>
    Task DownloadAsync(
        string remotePath,
        string localPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}
