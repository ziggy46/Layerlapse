using System.Net;
using Layerlapse.Core.Setup;

namespace Layerlapse.Core.Discovery;

/// <summary>
/// Parses the SSDP-style NOTIFY that Bambu Lab printers broadcast to 255.255.255.255:2021 every ~5 s:
/// <code>
/// NOTIFY * HTTP/1.1
/// Location: 192.168.1.50
/// NT: urn:bambulab-com:device:3dprinter:1
/// USN: &lt;serial&gt;
/// DevModel.bambu.com: BL-P001
/// DevName.bambu.com: &lt;printer name&gt;
/// DevVersion.bambu.com: 01.12.00.00
/// </code>
/// </summary>
public static class AnnouncementParser
{
    public const string PrinterNotificationType = "urn:bambulab-com:device:3dprinter:1";

    /// <summary>Returns null for anything that is not a printer announcement from a local address.</summary>
    public static DiscoveredPrinter? Parse(string payload, IPAddress sender, DateTimeOffset now)
    {
        var lines = payload.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("NOTIFY ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        if (!string.Equals(Header(headers, "NT"), PrinterNotificationType, StringComparison.OrdinalIgnoreCase)
            || Header(headers, "USN") is not { } serial)
        {
            return null;
        }

        var host = Header(headers, "Location") is { } location
            && IPAddress.TryParse(location, out var announced)
            && PrinterAddress.IsLocal(announced)
                ? announced
                : sender;
        if (!PrinterAddress.IsLocal(host))
        {
            return null;
        }

        var modelCode = Header(headers, "DevModel.bambu.com");
        return new DiscoveredPrinter(
            serial,
            host.ToString(),
            modelCode,
            PrinterModels.FromModelCode(modelCode) ?? PrinterModels.FromSerial(serial),
            Header(headers, "DevName.bambu.com"),
            Header(headers, "DevVersion.bambu.com"),
            DiscoverySource.Announcement,
            now);
    }

    private static string? Header(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}
