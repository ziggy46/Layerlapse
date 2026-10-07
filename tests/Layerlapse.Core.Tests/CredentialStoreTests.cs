using Layerlapse.Core.Credentials;

namespace Layerlapse.Core.Tests;

/// <summary>
/// Round trip through the real OS secret store. Opt-in (LAYERLAPSE_CREDENTIAL_TESTS=1) because CI runners
/// have no unlocked keyring and macOS may show an access prompt.
/// </summary>
[Trait("Category", "CredentialStore")]
public class CredentialStoreTests
{
    private sealed class CredentialFactAttribute : FactAttribute
    {
        public CredentialFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("LAYERLAPSE_CREDENTIAL_TESTS") != "1")
            {
                Skip = "Set LAYERLAPSE_CREDENTIAL_TESTS=1 to test the OS credential store.";
            }
        }
    }

    [CredentialFact]
    public async Task Saves_updates_reads_and_removes()
    {
        var store = CredentialStores.CreateDefault();
        Assert.True(store.IsPersistent, $"Expected a persistent store, got {store.Name}.");
        var id = "test-" + Guid.NewGuid().ToString("N");

        try
        {
            Assert.Null(await store.GetAsync(id));

            await store.SaveAsync(id, "first-Ü");
            Assert.Equal("first-Ü", await store.GetAsync(id));

            await store.SaveAsync(id, "second");
            Assert.Equal("second", await store.GetAsync(id));

            await store.RemoveAsync(id);
            Assert.Null(await store.GetAsync(id));
            await store.RemoveAsync(id);
        }
        finally
        {
            await store.RemoveAsync(id);
        }
    }
}
