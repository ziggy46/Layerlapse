namespace Layerlapse.Core.Discovery;

public enum DiscoverySource
{
    /// <summary>The printer's own UDP announcement (port 2021, every ~5 s).</summary>
    Announcement,

    /// <summary>Found by probing port 990 and reading the TLS certificate (no login).</summary>
    PortScan,
}

/// <summary>A printer found on the local network.</summary>
/// <param name="ModelCode">Raw model code from the announcement, such as "BL-P001". Null from a port scan.</param>
/// <param name="Model">Friendly model name when the code or serial prefix is known, otherwise null.</param>
/// <param name="Name">The printer's own name. Owners can rename printers, so this is not the model.</param>
public sealed record DiscoveredPrinter(
    string Serial,
    string Host,
    string? ModelCode,
    string? Model,
    string? Name,
    string? Firmware,
    DiscoverySource Source,
    DateTimeOffset LastSeen);
