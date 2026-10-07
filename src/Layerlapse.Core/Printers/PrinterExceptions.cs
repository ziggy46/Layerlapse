namespace Layerlapse.Core.Printers;

/// <summary>
/// A failure talking to the printer, with a message written for the person using the app.
/// Messages never contain the access code.
/// </summary>
public class PrinterConnectionException : IOException
{
    public PrinterConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Nothing answered on port 990 at that address, or it did not answer in time.</summary>
public sealed class PrinterUnreachableException(string host, Exception? innerException = null)
    : PrinterConnectionException(
        $"No printer answered at {host}. Check the IP address, that the printer is on, and that this computer is on the same network.",
        innerException)
{
    public string Host { get; } = host;
}

/// <summary>The printer refused the access code (FTP 530).</summary>
public sealed class PrinterAuthenticationException(Exception? innerException = null)
    : PrinterConnectionException(
        "The printer rejected the access code. Check it on the printer screen under Settings, then LAN Only Mode. It changes after a factory reset.",
        innerException);

/// <summary>
/// The printer presented a different certificate from the one pinned on first connect. This can mean a
/// factory reset or a different device at that address; the UI must warn loudly rather than reconnect.
/// </summary>
public sealed class PrinterCertificateMismatchException(string expected, string actual)
    : PrinterConnectionException(
        "The printer's security certificate has changed since you first connected. This happens after a factory reset, "
        + "but it could also mean a different device is using this address.")
{
    public string ExpectedFingerprint { get; } = expected;

    public string ActualFingerprint { get; } = actual;
}
