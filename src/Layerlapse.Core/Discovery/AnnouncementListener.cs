using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Layerlapse.Core.Discovery;

/// <summary>
/// Listens for printer announcements on UDP 2021. Printers broadcast to 255.255.255.255, so a plain
/// bind is enough. Address reuse lets this coexist with Bambu Studio listening on the same port.
/// </summary>
public sealed class AnnouncementListener(int port = AnnouncementListener.DefaultPort, TimeProvider? timeProvider = null)
{
    public const int DefaultPort = 2021;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Yields every valid announcement (including repeats) until the duration ends or cancellation.</summary>
    /// <exception cref="DiscoveryException">The port could not be opened.</exception>
    public async IAsyncEnumerable<DiscoveredPrinter> ListenAsync(TimeSpan duration, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var socket = Open(port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        var buffer = new byte[4096];

        while (true)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                yield break; // duration elapsed
            }

            var sender = ((IPEndPoint)received.RemoteEndPoint).Address;
            var text = Encoding.UTF8.GetString(buffer, 0, received.ReceivedBytes);
            if (AnnouncementParser.Parse(text, sender, _time.GetUtcNow()) is { } printer)
            {
                yield return printer;
            }
        }
    }

    private static Socket Open(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new DiscoveryException(
                $"Could not listen for printers on UDP port {port} ({e.SocketErrorCode}). Another app may be using it.", e);
        }
    }
}

public sealed class DiscoveryException(string message, Exception? innerException = null) : Exception(message, innerException);
