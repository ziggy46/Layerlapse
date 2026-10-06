using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Tls;

namespace Layerlapse.Core.Printers;

/// <summary>
/// Read-only implicit FTPS client for Bambu Lab printers.
///
/// The printer runs vsftpd with require_ssl_reuse: every data connection must resume the control
/// connection's TLS session. .NET's SslStream (and so FluentFTP) cannot do that on macOS or Linux,
/// so TLS here is BouncyCastle's managed implementation and the FTP layer is the minimal subset
/// the app needs. Only read commands are ever sent.
/// </summary>
public sealed partial class BambuFtpsClient : IPrinterClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly PrinterConnection _connection;
    private TcpClient? _controlSocket;
    private TlsClientProtocol? _controlTls;
    private TlsSession? _session;

    public BambuFtpsClient(PrinterConnection connection)
    {
        _connection = connection;
    }

    public string? CertificateFingerprint { get; private set; }

    /// <summary>The server's 220 greeting, for diagnostics.</summary>
    public string? Greeting { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _controlSocket = await OpenSocketAsync(_connection.FtpsPort, cancellationToken);
        var tlsClient = new PinnedTlsClient(AcceptControlCertificate);
        var tls = new TlsClientProtocol(_controlSocket.GetStream());
        try
        {
            await Task.Run(() => tls.Connect(tlsClient), cancellationToken);
        }
        catch (TlsFatalAlert e) when (e.AlertDescription == AlertDescription.bad_certificate && CertificateFingerprint is not null)
        {
            _controlSocket.Dispose();
            _controlSocket = null;
            throw new PrinterCertificateMismatchException(_connection.PinnedFingerprint!, CertificateFingerprint);
        }

        _controlTls = tls;
        _session = tlsClient.Session ?? throw new IOException("TLS handshake did not produce a session.");

        Greeting = (await ReadReplyAsync(cancellationToken)).Expect(220);
        (await CommandAsync($"USER {PrinterConnection.User}", cancellationToken)).Expect(331);
        (await CommandAsync($"PASS {_connection.AccessCode}", cancellationToken, isSecret: true)).Expect(230);
        (await CommandAsync("PBSZ 0", cancellationToken)).Expect(200);
        (await CommandAsync("PROT P", cancellationToken)).Expect(200);
        (await CommandAsync("TYPE I", cancellationToken)).Expect(200);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteFolder, CancellationToken cancellationToken = default)
    {
        var buffer = new MemoryStream();
        await TransferAsync($"LIST {CheckPath(remoteFolder)}", buffer, null, cancellationToken);
        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return UnixListingParser.Parse(text, remoteFolder, DateTime.Now);
    }

    public async Task DownloadAsync(
        string remotePath,
        string localPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await TransferAsync($"RETR {CheckPath(remotePath)}", file, progress, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_controlTls is not null)
        {
            try
            {
                await WriteLineAsync("QUIT", CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or TlsException or ObjectDisposedException)
            {
                // Best effort.
            }

            _controlTls.Close();
        }

        _controlSocket?.Dispose();
    }

    private async Task TransferAsync(string command, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var pasv = (await CommandAsync("PASV", cancellationToken)).Expect(227);
        var port = ParsePasvPort(pasv);

        // Connect to the control connection's address, never the one the server advertises.
        using var dataSocket = await OpenSocketAsync(port, cancellationToken);
        await WriteLineAsync(command, cancellationToken);

        // vsftpd sends 150 and then starts the TLS handshake on the data connection, so run them together.
        var dataTls = new TlsClientProtocol(dataSocket.GetStream());
        var dataClient = new PinnedTlsClient(fp => fp == CertificateFingerprint, _session);
        var handshake = Task.Run(() => dataTls.Connect(dataClient), cancellationToken);

        var preliminary = await ReadReplyAsync(cancellationToken);
        if (preliminary.Code is not (125 or 150))
        {
            dataSocket.Dispose();
            await handshake.ContinueWith(_ => { }, TaskScheduler.Default);
            throw new FtpReplyException(command.Split(' ')[0], preliminary);
        }

        await handshake;
        if (!dataClient.Resumed)
        {
            throw new IOException("Data connection did not resume the control TLS session.");
        }

        var buffer = new byte[81920];
        long total = 0;
        var stream = dataTls.Stream;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await Task.Run(() => stream.Read(buffer, 0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            progress?.Report(total);
        }

        dataTls.Close();
        (await ReadReplyAsync(cancellationToken)).Expect(226);
    }

    private bool AcceptControlCertificate(string fingerprint)
    {
        // Trust on first use: with no pin, accept and expose the fingerprint so the caller can save it.
        // With a pin, the printer must present exactly that certificate.
        CertificateFingerprint = fingerprint;
        return _connection.PinnedFingerprint is null
            || string.Equals(fingerprint, _connection.PinnedFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TcpClient> OpenSocketAsync(int port, CancellationToken cancellationToken)
    {
        var socket = new TcpClient { ReceiveTimeout = (int)Timeout.TotalMilliseconds, SendTimeout = (int)Timeout.TotalMilliseconds };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        await socket.ConnectAsync(_connection.Host, port, timeout.Token);
        return socket;
    }

    private async Task<FtpReply> CommandAsync(string command, CancellationToken cancellationToken, bool isSecret = false)
    {
        await WriteLineAsync(command, cancellationToken);
        var reply = await ReadReplyAsync(cancellationToken);
        reply.Command = isSecret ? command.Split(' ')[0] : command;
        return reply;
    }

    private Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        var stream = Control.Stream;
        return Task.Run(() => { stream.Write(bytes, 0, bytes.Length); stream.Flush(); }, cancellationToken);
    }

    private async Task<FtpReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        var first = await ReadLineAsync(cancellationToken);
        if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            throw new IOException($"Unexpected FTP reply: {first}");
        }

        var lines = new List<string> { first };
        if (first.Length > 3 && first[3] == '-')
        {
            var terminator = first[..3] + " ";
            string line;
            do
            {
                line = await ReadLineAsync(cancellationToken);
                lines.Add(line);
            }
            while (!line.StartsWith(terminator, StringComparison.Ordinal));
        }

        return new FtpReply(code, string.Join("\n", lines));
    }

    private Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var stream = Control.Stream;
        return Task.Run(() =>
        {
            var bytes = new List<byte>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var b = stream.ReadByte();
                if (b < 0)
                {
                    throw new IOException("The printer closed the connection.");
                }

                if (b == '\n')
                {
                    break;
                }

                if (b != '\r')
                {
                    bytes.Add((byte)b);
                }
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
        }, cancellationToken);
    }

    private TlsClientProtocol Control => _controlTls ?? throw new InvalidOperationException("Not connected.");

    private static string CheckPath(string path)
    {
        if (path.Contains('\r') || path.Contains('\n'))
        {
            throw new ArgumentException("Remote paths cannot contain line breaks.", nameof(path));
        }

        return path;
    }

    [GeneratedRegex(@"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)")]
    private static partial Regex PasvRegex();

    private static int ParsePasvPort(string reply)
    {
        var match = PasvRegex().Match(reply);
        if (!match.Success)
        {
            throw new IOException($"Could not parse PASV reply: {reply}");
        }

        return int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) * 256
            + int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
    }
}

internal sealed class FtpReply(int code, string text)
{
    public int Code { get; } = code;

    public string Text { get; } = text;

    public string? Command { get; set; }

    public string Expect(int expected) => Code == expected ? Text : throw new FtpReplyException(Command ?? "connect", this);
}

/// <summary>
/// The printer presented a different certificate from the one pinned on first connect. This can mean a
/// factory reset or a different device at that address; the UI must warn loudly rather than reconnect.
/// </summary>
public sealed class PrinterCertificateMismatchException(string expected, string actual)
    : IOException($"The printer's certificate changed. Expected SHA-256 {expected}, got {actual}.")
{
    public string ExpectedFingerprint { get; } = expected;

    public string ActualFingerprint { get; } = actual;
}

/// <summary>The printer answered an FTP command with an unexpected reply. Never contains the access code.</summary>
public sealed class FtpReplyException : IOException
{
    internal FtpReplyException(string command, FtpReply reply)
        : base($"{command} failed: {reply.Text}")
    {
        ReplyCode = reply.Code;
    }

    public int ReplyCode { get; }
}
