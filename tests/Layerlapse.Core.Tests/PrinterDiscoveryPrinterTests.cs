using System.Net;
using Layerlapse.Core.Discovery;
using Layerlapse.Core.Printers;
using Xunit.Abstractions;

namespace Layerlapse.Core.Tests;

/// <summary>Discovery against the real printer. Skipped unless LAYERLAPSE_IP and LAYERLAPSE_CODE are set.</summary>
[Trait("Category", "Printer")]
public class PrinterDiscoveryPrinterTests(ITestOutputHelper output)
{
    private static async Task<string> SerialFromLoginAsync()
    {
        await using var client = new BambuFtpsClient(new PrinterConnection(PrinterEnvironment.Host!, PrinterEnvironment.AccessCode!));
        await client.ConnectAsync();
        return client.Serial!;
    }

    [PrinterFact]
    public async Task Announcement_matches_the_logged_in_printer()
    {
        var serial = await SerialFromLoginAsync();
        var discovery = new PrinterDiscovery();

        DiscoveredPrinter? found = null;
        await foreach (var printer in discovery.DiscoverAsync(new DiscoveryOptions { ListenDuration = TimeSpan.FromSeconds(12), ScanIfNothingAnnounced = false, StopAtSerial = serial }))
        {
            found = printer;
        }

        Assert.NotNull(found);
        Assert.Equal(serial, found.Serial);
        Assert.Equal(PrinterEnvironment.Host, found.Host);
        Assert.Equal(DiscoverySource.Announcement, found.Source);
        output.WriteLine($"{found.Model} ({found.ModelCode}), firmware {found.Firmware}");
    }

    [PrinterFact]
    public async Task Port_probe_reads_the_serial_without_logging_in()
    {
        var serial = await SerialFromLoginAsync();

        var found = await new SubnetScanner().ProbeAsync(IPAddress.Parse(PrinterEnvironment.Host!), PrinterConnection.DefaultFtpsPort);

        Assert.NotNull(found);
        Assert.Equal(serial, found.Serial);
        Assert.Equal(DiscoverySource.PortScan, found.Source);
        Assert.Equal("X1 Carbon", found.Model);
    }

    [PrinterFact]
    public async Task Subnet_scan_finds_the_printer()
    {
        var serial = await SerialFromLoginAsync();
        var started = DateTime.UtcNow;

        var found = new List<DiscoveredPrinter>();
        await foreach (var printer in new SubnetScanner().ScanAsync())
        {
            found.Add(printer);
        }

        Assert.Contains(found, p => p.Serial == serial && p.Host == PrinterEnvironment.Host);
        output.WriteLine($"{SubnetScanner.CandidateAddresses().Count()} addresses scanned in {(DateTime.UtcNow - started).TotalSeconds:F1} s, {found.Count} printer(s)");
    }
}
