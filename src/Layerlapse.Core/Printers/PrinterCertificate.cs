namespace Layerlapse.Core.Printers;

/// <summary>
/// The printer's TLS certificate as seen during the handshake. On Bambu Lab printers the subject
/// common name is the printer's serial number (the same value it announces as USN over UDP).
/// </summary>
public sealed record PrinterCertificate(string Fingerprint, string? CommonName);
