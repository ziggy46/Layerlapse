using Org.BouncyCastle.Tls;

namespace Layerlapse.Core.Printers;

/// <summary>
/// Reads a server's TLS certificate with a handshake and nothing else: no FTP command, no credentials.
/// Used by discovery to recognise printers by their certificate.
/// </summary>
public static class PrinterCertificateProbe
{
    /// <summary>Returns the certificate, or null if the handshake fails.</summary>
    public static async Task<PrinterCertificate?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        PrinterCertificate? seen = null;
        var client = new PinnedTlsClient(certificate =>
        {
            seen = certificate;
            return true;
        });
        var tls = new TlsClientProtocol(stream);
        try
        {
            await Task.Run(() => tls.Connect(client), cancellationToken);
            tls.Close();
        }
        catch (Exception e) when (e is TlsException or IOException)
        {
            // The certificate may still have been seen before the failure.
        }

        return seen;
    }
}
