namespace Layerlapse.Core.Credentials;

public static class CredentialStores
{
    public const string ServiceName = "Layerlapse";

    /// <summary>The secret store for the current operating system.</summary>
    public static ICredentialStore CreateDefault()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainCredentialStore();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsLinux() && LibSecretCredentialStore.IsAvailable())
        {
            return new LibSecretCredentialStore();
        }

        return new InMemoryCredentialStore();
    }
}
