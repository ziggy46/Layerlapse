using System.Net;
using System.Net.Sockets;
using System.Text;
using Layerlapse.Core.Discovery;

namespace Layerlapse.Core.Tests;

public class AnnouncementParserTests
{
    // Captured from the owner's X1 Carbon on 2026-10-06 (address and serial replaced).
    public const string Captured =
        "NOTIFY * HTTP/1.1\r\n" +
        "Host: 239.255.255.250:1990\r\n" +
        "Server: UPnP/1.0\r\n" +
        "Location: 192.168.1.50\r\n" +
        "NT: urn:bambulab-com:device:3dprinter:1\r\n" +
        "NTS: ssdp:alive\r\n" +
        "USN: 00M000000000001\r\n" +
        "Cache-Control: max-age=1800\r\n" +
        "DevModel.bambu.com: BL-P001\r\n" +
        "DevName.bambu.com: Workshop X1C\r\n" +
        "DevSignal.bambu.com: -40\r\n" +
        "DevConnect.bambu.com: cloud\r\n" +
        "DevBind.bambu.com: occupied\r\n" +
        "Devseclink.bambu.com: secure\r\n" +
        "DevInf.bambu.com: wlan0\r\n" +
        "DevVersion.bambu.com: 01.12.00.00\r\n" +
        "DevCap.bambu.com: 1\r\n\r\n";

    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parses_the_captured_announcement()
    {
        var printer = AnnouncementParser.Parse(Captured, IPAddress.Parse("192.168.1.50"), Now);

        Assert.NotNull(printer);
        Assert.Equal("00M000000000001", printer.Serial);
        Assert.Equal("192.168.1.50", printer.Host);
        Assert.Equal("BL-P001", printer.ModelCode);
        Assert.Equal("X1 Carbon", printer.Model);
        Assert.Equal("Workshop X1C", printer.Name);
        Assert.Equal("01.12.00.00", printer.Firmware);
        Assert.Equal(DiscoverySource.Announcement, printer.Source);
    }

    [Fact]
    public void Unknown_model_code_leaves_model_unknown()
    {
        var payload = Captured.Replace("BL-P001", "Z9").Replace("00M000000000001", "ZZZ000000000001");

        var printer = AnnouncementParser.Parse(payload, IPAddress.Parse("192.168.1.50"), Now);

        Assert.Equal("Z9", printer!.ModelCode);
        Assert.Null(printer.Model);
    }

    [Fact]
    public void Model_is_never_taken_from_the_name()
    {
        var payload = Captured.Replace("BL-P001", "Z9").Replace("00M000000000001", "ZZZ000000000001").Replace("Workshop X1C", "X1 Carbon");

        Assert.Null(AnnouncementParser.Parse(payload, IPAddress.Parse("192.168.1.50"), Now)!.Model);
    }

    [Fact]
    public void Falls_back_to_sender_when_location_is_not_local()
    {
        var payload = Captured.Replace("Location: 192.168.1.50", "Location: 8.8.8.8");

        Assert.Equal("10.0.0.9", AnnouncementParser.Parse(payload, IPAddress.Parse("10.0.0.9"), Now)!.Host);
    }

    [Theory]
    [InlineData("M-SEARCH * HTTP/1.1\r\nNT: urn:bambulab-com:device:3dprinter:1\r\nUSN: X\r\n")]
    [InlineData("NOTIFY * HTTP/1.1\r\nNT: urn:schemas-upnp-org:device:MediaRenderer:1\r\nUSN: X\r\nLocation: 192.168.1.5\r\n")]
    [InlineData("NOTIFY * HTTP/1.1\r\nNT: urn:bambulab-com:device:3dprinter:1\r\nLocation: 192.168.1.5\r\n")]
    [InlineData("")]
    public void Ignores_other_packets(string payload)
    {
        Assert.Null(AnnouncementParser.Parse(payload, IPAddress.Parse("192.168.1.5"), Now));
    }

    [Fact]
    public void Ignores_non_local_senders_without_local_location()
    {
        var payload = Captured.Replace("Location: 192.168.1.50", "Location: 8.8.8.8");

        Assert.Null(AnnouncementParser.Parse(payload, IPAddress.Parse("8.8.4.4"), Now));
    }
}

public class PrinterModelsTests
{
    [Theory]
    [InlineData("BL-P001", "X1 Carbon")]
    [InlineData("bl-p001", "X1 Carbon")]
    [InlineData("Z9", null)]
    [InlineData(null, null)]
    public void Maps_model_codes(string? code, string? model) => Assert.Equal(model, PrinterModels.FromModelCode(code));

    [Theory]
    [InlineData("00M09D000000000", "X1 Carbon")]
    [InlineData("ZZZ", null)]
    [InlineData("0", null)]
    [InlineData(null, null)]
    public void Maps_serial_prefixes(string? serial, string? model) => Assert.Equal(model, PrinterModels.FromSerial(serial));
}

public class AnnouncementListenerTests
{
    [Fact]
    public async Task Receives_an_announcement_over_udp()
    {
        var port = FreeUdpPort();
        var listener = new AnnouncementListener(port);
        var received = new List<DiscoveredPrinter>();

        var listening = Task.Run(async () =>
        {
            await foreach (var printer in listener.ListenAsync(TimeSpan.FromSeconds(3)))
            {
                received.Add(printer);
                break;
            }
        });

        using var sender = new UdpClient();
        var bytes = Encoding.UTF8.GetBytes(AnnouncementParserTests.Captured);
        for (var i = 0; i < 20 && !listening.IsCompleted; i++)
        {
            await sender.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port));
            await Task.Delay(100);
        }

        await listening;
        Assert.Equal("00M000000000001", Assert.Single(received).Serial);
    }

    [Fact]
    public async Task Stops_after_the_duration()
    {
        var listener = new AnnouncementListener(FreeUdpPort());
        var started = DateTime.UtcNow;

        await foreach (var _ in listener.ListenAsync(TimeSpan.FromMilliseconds(300)))
        {
        }

        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}

public class PrinterDiscoveryTests
{
    [Fact]
    public async Task Does_not_scan_when_scanning_is_off()
    {
        using var probe = new UdpClient(0);
        var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();
        var discovery = new PrinterDiscovery(new AnnouncementListener(port));

        var found = new List<DiscoveredPrinter>();
        await foreach (var p in discovery.DiscoverAsync(new DiscoveryOptions { ListenDuration = TimeSpan.FromMilliseconds(200), ScanIfNothingAnnounced = false }))
        {
            found.Add(p);
        }

        Assert.Empty(found);
    }
}
