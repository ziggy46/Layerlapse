using System.Collections.Concurrent;

namespace Layerlapse.Core.Credentials;

/// <summary>
/// Session-only store, used when the OS has no secret store (for example Linux without a Secret Service
/// daemon) and in tests. Codes are forgotten when the app quits; never written to disk.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _codes = new();

    public string Name => "this session only";

    public bool IsPersistent => false;

    public Task<string?> GetAsync(string printerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_codes.TryGetValue(printerId, out var code) ? code : null);

    public Task SaveAsync(string printerId, string accessCode, CancellationToken cancellationToken = default)
    {
        _codes[printerId] = accessCode;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string printerId, CancellationToken cancellationToken = default)
    {
        _codes.TryRemove(printerId, out _);
        return Task.CompletedTask;
    }
}
