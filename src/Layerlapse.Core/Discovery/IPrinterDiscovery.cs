namespace Layerlapse.Core.Discovery;

public sealed record DiscoveryOptions
{
    /// <summary>How long to listen for announcements. Printers announce every ~5 s.</summary>
    public TimeSpan ListenDuration { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Probe port 990 on the local /24 networks if no announcement arrived. Off by default: the app only
    /// scans when the user asks, because a scan contacts every address on the network.
    /// </summary>
    public bool ScanIfNothingAnnounced { get; init; }

    /// <summary>Stop as soon as this serial is found (used to find a printer whose IP changed).</summary>
    public string? StopAtSerial { get; init; }
}

/// <summary>Finds printers on the local network. Read-only: never logs in.</summary>
public interface IPrinterDiscovery
{
    /// <summary>
    /// Streams printers as they are found, once per serial. Repeated announcements are not repeated here.
    /// </summary>
    IAsyncEnumerable<DiscoveredPrinter> DiscoverAsync(DiscoveryOptions options, CancellationToken cancellationToken = default);
}
