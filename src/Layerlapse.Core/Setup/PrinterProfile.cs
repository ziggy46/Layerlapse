namespace Layerlapse.Core.Setup;

/// <summary>
/// A saved printer. Holds no secrets: the access code lives in the credential store under <see cref="Id"/>.
/// </summary>
/// <param name="Id">Credential store key: the serial when known, otherwise "host:&lt;ip&gt;".</param>
/// <param name="Serial">From the certificate's common name.</param>
/// <param name="Host">Last known IP address. May drift; the serial identifies the printer.</param>
/// <param name="PinnedFingerprint">SHA-256 of the certificate seen on the first successful login.</param>
/// <param name="Model">Friendly model name, from discovery, the serial prefix or the user's choice.</param>
/// <param name="ModelCode">Raw model code from the printer's announcement, such as "BL-P001".</param>
/// <param name="Name">The printer's own name from its announcement.</param>
/// <param name="Firmware">Firmware version from the announcement.</param>
public sealed record PrinterProfile(
    string Id,
    string? Serial,
    string Host,
    string PinnedFingerprint,
    DateTimeOffset LastConnected,
    string? Model = null,
    string? ModelCode = null,
    string? Name = null,
    string? Firmware = null)
{
    /// <summary>What to call the printer in the UI.</summary>
    public string DisplayName => Name ?? Model ?? Serial ?? Host;

    public static string IdFor(string? serial, string host) =>
        string.IsNullOrWhiteSpace(serial) ? $"host:{host}" : serial;

    /// <summary>Copies the announcement's details (and its address) onto this profile.</summary>
    public PrinterProfile With(Discovery.DiscoveredPrinter found) => this with
    {
        Host = found.Host,
        Model = found.Model ?? Model,
        ModelCode = found.ModelCode ?? ModelCode,
        Name = found.Name ?? Name,
        Firmware = found.Firmware ?? Firmware,
    };
}
