using System.Net;
using System.Net.Sockets;

namespace Layerlapse.Core.Setup;

/// <summary>
/// The app only talks to printers on the local network: private-range or link-local IPv4 addresses.
/// </summary>
public static class PrinterAddress
{
    /// <summary>Returns null when <paramref name="input"/> is acceptable, otherwise a message for the user.</summary>
    public static string? Validate(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return "Enter the printer's IP address. It is on the printer screen under Settings, then Network.";
        }

        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork || text.Count(c => c == '.') != 3)
        {
            return "Enter an IPv4 address such as 192.168.1.50.";
        }

        return IsLocal(address)
            ? null
            : "That address is not on a local network. Layerlapse only connects to printers on your own network (addresses starting 10., 172.16–31., 192.168. or 169.254.).";
    }

    public static bool IsLocal(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && (
            b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254));
    }
}
