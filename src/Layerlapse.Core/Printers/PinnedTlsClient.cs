using System.Security.Cryptography;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security;

namespace Layerlapse.Core.Printers;

/// <summary>
/// TLS 1.2 client for the printer's FTPS server. The control connection establishes a session and each data
/// connection offers it again: the X1's vsftpd (require_ssl_reuse) answers "522 SSL connection failed: session
/// reuse required" without it, while other models may start a fresh session, which is checked like the first.
/// The printer's certificate has no trusted chain, so it is checked by SHA-256 fingerprint instead.
/// </summary>
internal sealed class PinnedTlsClient : DefaultTlsClient
{
    private readonly Func<PrinterCertificate, bool> _acceptCertificate;
    private readonly TlsSession? _sessionToResume;

    public PinnedTlsClient(Func<PrinterCertificate, bool> acceptCertificate, TlsSession? sessionToResume = null)
        : base(new BcTlsCrypto(new SecureRandom()))
    {
        _acceptCertificate = acceptCertificate;
        _sessionToResume = sessionToResume;
    }

    /// <summary>The negotiated session, available once the handshake completes.</summary>
    public TlsSession? Session { get; private set; }

    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv12.Only();

    public override TlsSession? GetSessionToResume() => _sessionToResume;

    public override TlsAuthentication GetAuthentication() => new FingerprintAuthentication(_acceptCertificate);

    public override void NotifyHandshakeComplete()
    {
        base.NotifyHandshakeComplete();
        Session = m_context.Session;
    }

    public static string Fingerprint(byte[] derCertificate) => Convert.ToHexString(SHA256.HashData(derCertificate));

    private static string? Issuer(byte[] derCertificate)
    {
        try
        {
            return new Org.BouncyCastle.X509.X509Certificate(derCertificate).IssuerDN.ToString();
        }
        catch (Exception e) when (e is ArgumentException or Org.BouncyCastle.Security.Certificates.CertificateException or IOException)
        {
            return null;
        }
    }

    private static string? CommonName(byte[] derCertificate)
    {
        try
        {
            var subject = new Org.BouncyCastle.X509.X509Certificate(derCertificate).SubjectDN;
            return subject.GetValueList(X509Name.CN).FirstOrDefault();
        }
        catch (Exception e) when (e is ArgumentException or Org.BouncyCastle.Security.Certificates.CertificateException or IOException)
        {
            return null;
        }
    }

    private sealed class FingerprintAuthentication(Func<PrinterCertificate, bool> acceptCertificate) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var chain = serverCertificate.Certificate;
            if (chain is null || chain.IsEmpty)
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }

            var der = chain.GetCertificateAt(0).GetEncoded();
            if (!acceptCertificate(new PrinterCertificate(Fingerprint(der), CommonName(der), Issuer(der))))
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }
        }

        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
    }
}
