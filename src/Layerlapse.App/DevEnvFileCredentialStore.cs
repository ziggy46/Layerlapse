#if DEBUG
using Layerlapse.Core.Credentials;

namespace Layerlapse.App;

/// <summary>
/// Development builds only: reads the access code from the developer's ~/.config/layerlapse/env file
/// (LAYERLAPSE_CODE=...), so ad-hoc signed test builds do not trigger a Keychain prompt after every rebuild.
/// Never writes anything; saving and removing do nothing. Release builds always use the OS secret store.
/// </summary>
internal sealed class DevEnvFileCredentialStore(string filePath) : ICredentialStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "layerlapse", "env");

    public string Name => "the development env file";

    public bool IsPersistent => true;

    public Task<string?> GetAsync(string printerId, CancellationToken cancellationToken = default)
    {
        foreach (var raw in File.ReadLines(filePath))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            if (line.StartsWith("LAYERLAPSE_CODE=", StringComparison.Ordinal))
            {
                return Task.FromResult<string?>(line["LAYERLAPSE_CODE=".Length..].Trim().Trim('"', '\''));
            }
        }

        return Task.FromResult<string?>(null);
    }

    public Task SaveAsync(string printerId, string accessCode, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RemoveAsync(string printerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
#endif
