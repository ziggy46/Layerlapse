namespace Layerlapse.Core.Printers;

/// <summary>
/// One connection to a printer, shared by everything that reads from it. Operations run one at a time
/// (the plan's "one connection at a time"); the connection opens on first use, is retried once if it
/// dropped, and closes after a minute of inactivity (vsftpd drops idle sessions after five).
/// </summary>
public sealed class PrinterSession : IAsyncDisposable
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);

    private readonly PrinterConnection _connection;
    private readonly Func<PrinterConnection, IPrinterClient> _clientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPrinterClient? _client;
    private long _generation;
    private bool _disposed;

    public PrinterSession(PrinterConnection connection, Func<PrinterConnection, IPrinterClient> clientFactory)
    {
        _connection = connection;
        _clientFactory = clientFactory;
    }

    public string Host => _connection.Host;

    public async Task<T> RunAsync<T>(Func<IPrinterClient, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                return await operation(await ClientAsync(cancellationToken));
            }
            catch (IOException e) when (e is not (PrinterConnectionException or FtpReplyException)
                                        && !cancellationToken.IsCancellationRequested)
            {
                // The connection may have been dropped (idle timeout, Wi-Fi blip): reconnect once and retry.
                // An FTP reply such as "550 not found" means the connection is fine, and a failure to connect at all
                // (unreachable, wrong code, certificate) already has its own message, so neither is retried.
                await CloseClientAsync();
                return await operation(await ClientAsync(cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // A cancelled transfer leaves the connection in an unknown state.
            await CloseClientAsync();
            throw;
        }
        finally
        {
            var generation = Interlocked.Increment(ref _generation);
            _gate.Release();
            _ = CloseWhenIdleAsync(generation);
        }
    }

    public Task RunAsync(Func<IPrinterClient, Task> operation, CancellationToken cancellationToken = default) =>
        RunAsync(async client =>
        {
            await operation(client);
            return true;
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await CloseClientAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IPrinterClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        var client = _clientFactory(_connection);
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }

        _client = client;
        return client;
    }

    private async Task CloseClientAsync()
    {
        if (_client is { } client)
        {
            _client = null;
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private async Task CloseWhenIdleAsync(long generation)
    {
        await Task.Delay(IdleClose);
        if (Interlocked.Read(ref _generation) != generation || !await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (Interlocked.Read(ref _generation) == generation)
            {
                await CloseClientAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
