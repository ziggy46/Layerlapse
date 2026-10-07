using System.Runtime.CompilerServices;
using Layerlapse.Core.Discovery;

namespace Layerlapse.Core.Tests;

/// <summary>Returns a fixed list of printers, honouring StopAtSerial.</summary>
internal sealed class FakeDiscovery : IPrinterDiscovery
{
    public List<DiscoveredPrinter> Printers { get; } = [];

    public int Calls { get; private set; }

    public async IAsyncEnumerable<DiscoveredPrinter> DiscoverAsync(DiscoveryOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        foreach (var printer in Printers.ToList())
        {
            await Task.Yield();
            yield return printer;
            if (options.StopAtSerial == printer.Serial)
            {
                yield break;
            }
        }
    }

    public static DiscoveredPrinter Announced(string serial = "00M000000000001", string host = "192.168.1.50", string? modelCode = "BL-P001", string? name = "Workshop X1C") =>
        new(serial, host, modelCode, PrinterModels.FromModelCode(modelCode) ?? PrinterModels.FromSerial(serial), name, "01.12.00.00", DiscoverySource.Announcement, DateTimeOffset.UtcNow);
}
