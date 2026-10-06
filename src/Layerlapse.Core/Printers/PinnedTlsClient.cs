using System.Security.Cryptography;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.Security;

namespace Layerlapse.Core.Printers;

/// <summary>
/// TLS 1.2 client for the printer's FTPS server (vsftpd with require_ssl_reuse).
/// The control connection establishes a session; each data connection must resume that same
/// session or the server answers "522 SSL connection failed: session reuse required".
/// The printer's certificate has no trusted chain, so it is checked by SHA-256 fingerprint instead.
/// </summary>
internal sealed class PinnedTlsClient : DefaultTlsClient
{
    private readonly Func<string, bool> _acceptFingerprint;
    private readonly TlsSession? _sessionToResume;

    public PinnedTlsClient(Func<string, bool> acceptFingerprint, TlsSession? sessionToResume = null)
        : base(new BcTlsCrypto(new SecureRandom()))
    {
        _acceptFingerprint = acceptFingerprint;
        _sessionToResume = sessionToResume;
    }

    /// <summary>The negotiated session, available once the handshake completes.</summary>
    public TlsSession? Session { get; private set; }

    /// <summary>Whether the handshake resumed <c>sessionToResume</c> rather than starting a new session.</summary>
    public bool Resumed { get; private set; }

    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv12.Only();

    public override TlsSession? GetSessionToResume() => _sessionToResume;

    public override TlsAuthentication GetAuthentication() => new FingerprintAuthentication(_acceptFingerprint);

    public override void NotifyHandshakeComplete()
    {
        base.NotifyHandshakeComplete();
        Session = m_context.Session;
        Resumed = _sessionToResume is not null
            && Session is not null
            && _sessionToResume.SessionID.AsSpan().SequenceEqual(Session.SessionID);
    }

    public static string Fingerprint(byte[] derCertificate) => Convert.ToHexString(SHA256.HashData(derCertificate));

    private sealed class FingerprintAuthentication(Func<string, bool> acceptFingerprint) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var chain = serverCertificate.Certificate;
            if (chain is null || chain.IsEmpty)
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }

            var fingerprint = Fingerprint(chain.GetCertificateAt(0).GetEncoded());
            if (!acceptFingerprint(fingerprint))
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }
        }

        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
    }
}
