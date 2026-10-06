using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentFTP;

namespace Layerlapse.Core.Printers;

/// <summary>
/// Implicit FTPS client built on FluentFTP. DOES NOT WORK against the printer: login succeeds, but every
/// data connection fails with "522 SSL connection failed: session reuse required" because .NET's SslStream
/// cannot resume the control connection's TLS session (seen on macOS and Linux, .NET 10, FluentFTP 55).
/// Kept only to reproduce that finding (Layerlapse.Spike --library fluentftp); use <see cref="BambuFtpsClient"/>.
/// See docs/FINDINGS.md.
/// </summary>
public sealed class FtpsPrinterClient : IPrinterClient
{
    private readonly PrinterConnection _connection;
    private readonly AsyncFtpClient _ftp;

    public FtpsPrinterClient(PrinterConnection connection)
    {
        _connection = connection;
        _ftp = new AsyncFtpClient(connection.Host, PrinterConnection.User, connection.AccessCode, connection.FtpsPort);
        _ftp.Encoding = Encoding.UTF8;
        _ftp.Config.EncryptionMode = FtpEncryptionMode.Implicit;
        _ftp.Config.DataConnectionEncryption = true;
        _ftp.Config.DataConnectionType = FtpDataConnectionType.PASV;
        _ftp.Config.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        _ftp.Config.ValidateAnyCertificate = false;
        _ftp.Config.RetryAttempts = 1;
        _ftp.ValidateCertificate += OnValidateCertificate;
    }

    public string? CertificateFingerprint { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _ftp.Connect(cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteFolder, CancellationToken cancellationToken = default)
    {
        var items = await _ftp.GetListing(remoteFolder, cancellationToken);
        return items
            .Where(i => i.Type is FtpObjectType.File or FtpObjectType.Directory)
            .Select(i => new RemoteEntry(i.Name, i.FullName, i.Size, i.Modified, i.Type == FtpObjectType.Directory))
            .ToList();
    }

    public async Task DownloadAsync(
        string remotePath,
        string localPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IProgress<FtpProgress>? ftpProgress = progress is null ? null : new Progress<FtpProgress>(p => progress.Report(p.TransferredBytes));
        var status = await _ftp.DownloadFile(localPath, remotePath, FtpLocalExists.Overwrite, FtpVerify.None, ftpProgress, cancellationToken);
        if (status != FtpStatus.Success)
        {
            throw new IOException($"Download of '{remotePath}' finished with status {status}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ftp.ValidateCertificate -= OnValidateCertificate;
        await _ftp.DisposeAsync();
    }

    private void OnValidateCertificate(FluentFTP.Client.BaseClient.BaseFtpClient control, FtpSslValidationEventArgs e)
    {
        if (e.Certificate is null)
        {
            e.Accept = false;
            return;
        }

        var fingerprint = Fingerprint(e.Certificate);
        CertificateFingerprint ??= fingerprint;

        // Trust on first use: with no pin, accept and expose the fingerprint so the caller can save it.
        // With a pin, the control and every data connection must present that exact certificate.
        e.Accept = _connection.PinnedFingerprint is null
            ? fingerprint == CertificateFingerprint
            : string.Equals(fingerprint, _connection.PinnedFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public static string Fingerprint(X509Certificate certificate)
    {
        var hash = SHA256.HashData(certificate.GetRawCertData());
        return Convert.ToHexString(hash);
    }
}
