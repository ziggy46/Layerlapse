using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;

namespace Layerlapse.Core.Discovery;

/// <summary>
/// Fallback discovery: tries port 990 on every address of this computer's local /24 networks and reads the
/// TLS certificate of anything that answers. A Bambu Lab printer's certificate is issued by "BBL CA" and
/// its common name is the serial. Only a TLS handshake happens; no FTP command or credential is sent.
/// </summary>
public sealed class SubnetScanner(TimeProvider? timeProvider = null)
{
    private const int MaxConcurrent = 64;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async IAsyncEnumerable<DiscoveredPrinter> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var addresses = CandidateAddresses().ToList();
        var found = Channel.CreateUnbounded<DiscoveredPrinter>();
        var gate = new SemaphoreSlim(MaxConcurrent);

        var producer = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(addresses.Select(async address =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        if (await ProbeAsync(address, PrinterConnection.DefaultFtpsPort, cancellationToken) is { } printer)
                        {
                            await found.Writer.WriteAsync(printer, cancellationToken);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }
            finally
            {
                found.Writer.TryComplete();
            }
        }, cancellationToken);

        await foreach (var printer in found.Reader.ReadAllAsync(cancellationToken))
        {
            yield return printer;
        }

        await producer;
    }

    /// <summary>Connects to one address and returns the printer if it presents a Bambu Lab certificate.</summary>
    public async Task<DiscoveredPrinter?> ProbeAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        using var socket = new TcpClient();
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connect.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(address, port, connect.Token);
            }

            socket.ReceiveTimeout = socket.SendTimeout = (int)HandshakeTimeout.TotalMilliseconds;
            var certificate = await PrinterCertificateProbe.ReadAsync(socket.GetStream(), cancellationToken);
            if (certificate is not { CommonName: { Length: 15 } serial } || certificate.Issuer?.Contains("BBL", StringComparison.Ordinal) != true)
            {
                return null;
            }

            return new DiscoveredPrinter(serial, address.ToString(), null, PrinterModels.FromSerial(serial), null, null, DiscoverySource.PortScan, _time.GetUtcNow());
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Every other address in each local private /24 (larger networks are limited to this computer's /24).</summary>
    public static IEnumerable<IPAddress> CandidateAddresses()
    {
        var seen = new HashSet<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var own = unicast.Address;
                if (own.AddressFamily != AddressFamily.InterNetwork || !PrinterAddress.IsLocal(own) || own.GetAddressBytes()[0] == 169)
                {
                    continue;
                }

                var b = own.GetAddressBytes();
                for (var last = 1; last < 255; last++)
                {
                    var candidate = new IPAddress([b[0], b[1], b[2], (byte)last]);
                    if (!candidate.Equals(own) && seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }
}
