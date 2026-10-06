namespace Layerlapse.Core.Printers;

/// <summary>
/// How to reach a printer. <see cref="PinnedFingerprint"/> is the SHA-256 certificate fingerprint saved
/// on first connect; when set, any other certificate is rejected.
/// </summary>
public sealed record PrinterConnection(string Host, string AccessCode, string? PinnedFingerprint = null, int FtpsPort = PrinterConnection.DefaultFtpsPort)
{
    public const int DefaultFtpsPort = 990;
    public const string User = "bblp";

    // Keep the access code out of logs and exception messages.
    public override string ToString() => $"PrinterConnection {{ Host = {Host}, Pinned = {PinnedFingerprint is not null} }}";
}
