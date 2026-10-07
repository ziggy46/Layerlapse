using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Tls;

namespace Layerlapse.Core.Printers;

/// <summary>
/// Implicit FTPS client for Bambu Lab printers. Read-only except <see cref="DeleteAsync"/>, which is limited to
/// timelapse files by <see cref="DeletePolicy"/>.
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

    public string? Serial { get; private set; }

    /// <summary>The server's 220 greeting, for diagnostics.</summary>
    public string? Greeting { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _controlSocket = await OpenSocketAsync(_connection.FtpsPort, cancellationToken);
        }
        catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new PrinterUnreachableException(_connection.Host, e);
        }

        var tlsClient = new PinnedTlsClient(AcceptControlCertificate);
        var tls = new TlsClientProtocol(_controlSocket.GetStream());
        try
        {
            await Task.Run(() => tls.Connect(tlsClient), cancellationToken);
        }
        catch (TlsFatalAlert e) when (e.AlertDescription == AlertDescription.bad_certificate && CertificateFingerprint is not null)
        {
            CloseSocket();
            throw new PrinterCertificateMismatchException(_connection.PinnedFingerprint!, CertificateFingerprint);
        }
        catch (Exception e) when (e is TlsException or IOException)
        {
            CloseSocket();
            throw new PrinterConnectionException(
                $"Something at {_connection.Host} answered on port {_connection.FtpsPort}, but a secure connection could not be set up. It may not be a Bambu Lab printer.",
                e);
        }

        _controlTls = tls;
        _session = tlsClient.Session ?? throw new IOException("TLS handshake did not produce a session.");

        Greeting = (await ReadReplyAsync(cancellationToken)).Expect(220);
        (await CommandAsync($"USER {PrinterConnection.User}", cancellationToken)).Expect(331);
        var login = await CommandAsync($"PASS {_connection.AccessCode}", cancellationToken, isSecret: true);
        if (login.Code == 530)
        {
            throw new PrinterAuthenticationException(new FtpReplyException("PASS", login));
        }

        login.Expect(230);
        (await CommandAsync("PBSZ 0", cancellationToken)).Expect(200);
        (await CommandAsync("PROT P", cancellationToken)).Expect(200);
        (await CommandAsync("TYPE I", cancellationToken)).Expect(200);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string remoteFolder, CancellationToken cancellationToken = default)
    {
        var buffer = new MemoryStream();
        await TransferAsync($"LIST {CheckPath(remoteFolder)}", buffer, null, cancellationToken);
        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        return UnixListingParser.Parse(text, remoteFolder, DateTime.UtcNow);
    }

    public async Task<DateTime?> GetModifiedTimeAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var reply = await CommandAsync($"MDTM {CheckPath(remotePath)}", cancellationToken);
        if (reply.Code != 213)
        {
            return null;
        }

        // "213 20261004234443" (UTC, optionally with fractional seconds)
        var value = reply.Text.Length > 4 ? reply.Text[4..].Trim() : "";
        return DateTime.TryParseExact(
            value.Split('.')[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
    }

    public Task DownloadAsync(
        string remotePath,
        string localPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(remotePath, localPath, 0, progress, cancellationToken);

    public async Task DownloadAsync(
        string remotePath,
        string localPath,
        long resumeFrom,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resumeFrom);
        await using var file = new FileStream(localPath, resumeFrom > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        if (resumeFrom > 0)
        {
            // Keep exactly the bytes already received, then ask the printer to continue from there.
            file.SetLength(resumeFrom);
            file.Seek(resumeFrom, SeekOrigin.Begin);
        }

        await TransferAsync($"RETR {CheckPath(remotePath)}", file, progress, cancellationToken, restartAt: resumeFrom);
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

    public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        DeletePolicy.Check(remotePath);
        (await CommandAsync($"DELE {CheckPath(remotePath)}", cancellationToken)).Expect(250);
    }

    public async Task<byte[]> ReadRangeAsync(string remotePath, long offset, int length, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var buffer = new MemoryStream(length);
        await TransferAsync($"RETR {CheckPath(remotePath)}", buffer, null, cancellationToken, restartAt: offset, maxBytes: length);
        return buffer.ToArray();
    }

    private async Task TransferAsync(string command, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken, long restartAt = 0, long? maxBytes = null)
    {
        var pasv = (await CommandAsync("PASV", cancellationToken)).Expect(227);
        var port = ParsePasvPort(pasv);

        // Connect to the control connection's address, never the one the server advertises.
        using var dataSocket = await OpenSocketAsync(port, cancellationToken);
        if (restartAt > 0)
        {
            // REST goes directly before the transfer command it applies to.
            (await CommandAsync($"REST {restartAt}", cancellationToken)).Expect(350);
        }

        await WriteLineAsync(command, cancellationToken);

        // vsftpd answers 150 before it starts TLS on the data connection, so read the reply first and only then
        // shake hands. Starting the handshake early and abandoning it (for example after "550 not found")
        // makes BouncyCastle invalidate the session, and every later transfer would fail to resume it.
        var preliminary = await ReadReplyAsync(cancellationToken);
        if (preliminary.Code is not (125 or 150))
        {
            throw new FtpReplyException(command.Split(' ')[0], preliminary);
        }

        var dataTls = new TlsClientProtocol(dataSocket.GetStream());
        var dataClient = new PinnedTlsClient(c => c.Fingerprint == CertificateFingerprint, _session);
        await Task.Run(() => dataTls.Connect(dataClient), cancellationToken);
        if (!dataClient.Resumed)
        {
            throw new IOException("Data connection did not resume the control TLS session.");
        }

        var buffer = new byte[81920];
        var total = restartAt;
        long received = 0;
        var stream = dataTls.Stream;
        var stoppedEarly = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = maxBytes is { } max ? (int)Math.Min(buffer.Length, max - received) : buffer.Length;
            if (want <= 0)
            {
                stoppedEarly = true;
                break;
            }

            var read = await Task.Run(() => stream.Read(buffer, 0, want), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            received += read;
            progress?.Report(total);
        }

        if (stoppedEarly)
        {
            // We have the bytes we asked for: drop the data connection. vsftpd then reports the aborted
            // transfer (426), or 226 if it had already sent everything.
            dataSocket.Dispose();
            var reply = await ReadReplyAsync(cancellationToken);
            if (reply.Code is not (226 or 426 or 451))
            {
                throw new FtpReplyException(command.Split(' ')[0], reply);
            }

            return;
        }

        dataTls.Close();
        (await ReadReplyAsync(cancellationToken)).Expect(226);
    }

    private bool AcceptControlCertificate(PrinterCertificate certificate)
    {
        // Trust on first use: with no pin, accept and expose the fingerprint so the caller can save it
        // once login succeeds. With a pin, the printer must present exactly that certificate.
        CertificateFingerprint = certificate.Fingerprint;
        Serial = certificate.CommonName;
        return _connection.PinnedFingerprint is null
            || string.Equals(certificate.Fingerprint, _connection.PinnedFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private void CloseSocket()
    {
        _controlSocket?.Dispose();
        _controlSocket = null;
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

/// <summary>The printer answered an FTP command with an unexpected reply. Never contains the access code.</summary>
public sealed class FtpReplyException : IOException
{
    internal FtpReplyException(string command, FtpReply reply)
        : this(command, reply.Code, reply.Text)
    {
    }

    public FtpReplyException(string command, int replyCode, string replyText)
        : base($"{command} failed: {replyText}")
    {
        ReplyCode = replyCode;
    }

    public int ReplyCode { get; }
}
