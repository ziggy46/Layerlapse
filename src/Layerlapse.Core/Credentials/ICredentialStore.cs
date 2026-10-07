namespace Layerlapse.Core.Credentials;

/// <summary>
/// Keeps each printer's access code in the operating system's secret store, keyed by printer id
/// (the serial number when known). Never write the code anywhere else.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Where codes are kept, for messages ("macOS Keychain", ...).</summary>
    string Name { get; }

    /// <summary>False when codes only live for this session (no secret store available).</summary>
    bool IsPersistent { get; }

    /// <exception cref="CredentialStoreException">The store could not be read.</exception>
    Task<string?> GetAsync(string printerId, CancellationToken cancellationToken = default);

    /// <exception cref="CredentialStoreException">The store could not be written.</exception>
    Task SaveAsync(string printerId, string accessCode, CancellationToken cancellationToken = default);

    /// <summary>Removes the code. Does nothing if there is none.</summary>
    Task RemoveAsync(string printerId, CancellationToken cancellationToken = default);
}

public sealed class CredentialStoreException(string message, Exception? innerException = null)
    : Exception(message, innerException);
