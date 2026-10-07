using System.Runtime.CompilerServices;

namespace Layerlapse.Core.Discovery;

/// <summary>
/// Listens for announcements first; if none arrive, falls back to the port 990 scan.
/// Each serial is yielded once.
/// </summary>
public sealed class PrinterDiscovery(AnnouncementListener? listener = null, SubnetScanner? scanner = null) : IPrinterDiscovery
{
    private readonly AnnouncementListener _listener = listener ?? new AnnouncementListener();
    private readonly SubnetScanner _scanner = scanner ?? new SubnetScanner();

    public async IAsyncEnumerable<DiscoveredPrinter> DiscoverAsync(DiscoveryOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var printer in _listener.ListenAsync(options.ListenDuration, cancellationToken))
        {
            if (seen.Add(printer.Serial))
            {
                yield return printer;
                if (IsTarget(printer, options))
                {
                    yield break;
                }
            }
        }

        if (seen.Count > 0 || !options.ScanIfNothingAnnounced)
        {
            yield break;
        }

        await foreach (var printer in _scanner.ScanAsync(cancellationToken))
        {
            if (seen.Add(printer.Serial))
            {
                yield return printer;
                if (IsTarget(printer, options))
                {
                    yield break;
                }
            }
        }
    }

    private static bool IsTarget(DiscoveredPrinter printer, DiscoveryOptions options) =>
        options.StopAtSerial is { } serial && string.Equals(printer.Serial, serial, StringComparison.OrdinalIgnoreCase);
}
