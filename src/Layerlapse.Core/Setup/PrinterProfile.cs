namespace Layerlapse.Core.Setup;

/// <summary>
/// A saved printer. Holds no secrets: the access code lives in the credential store under <see cref="Id"/>.
/// </summary>
/// <param name="Id">Credential store key: the serial when known, otherwise "host:&lt;ip&gt;".</param>
/// <param name="Serial">From the certificate's common name.</param>
/// <param name="Host">Last known IP address. May drift; the serial identifies the printer.</param>
/// <param name="PinnedFingerprint">SHA-256 of the certificate seen on the first successful login.</param>
public sealed record PrinterProfile(
    string Id,
    string? Serial,
    string Host,
    string PinnedFingerprint,
    DateTimeOffset LastConnected)
{
    public static string IdFor(string? serial, string host) =>
        string.IsNullOrWhiteSpace(serial) ? $"host:{host}" : serial;
}
