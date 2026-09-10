using CDSI.Agent.Infrastructure.Security;

namespace CDSI.Agent.Infrastructure.Tests.Security;

public sealed class WindowsCredentialSecretStoreTests
{
    [Fact]
    public async Task ExistsAndDeleteAsync_HandleAnUnknownCredentialWithoutCreatingState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialSecretStore();
        var key = $"test-{Guid.NewGuid():N}";

        Assert.False(await store.ExistsAsync(key));
        await store.DeleteAsync(key);
        Assert.False(await store.ExistsAsync(key));
    }

    [Fact]
    public async Task StoreRetrieveAndDeleteAsync_RoundTripsTheSecret()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialSecretStore();
        var key = $"test-{Guid.NewGuid():N}";
        const string secret = "temporary-test-secret";
        try
        {
            await store.StoreAsync(key, secret);

            Assert.Equal(secret, await store.RetrieveAsync(key));
        }
        finally
        {
            await store.DeleteAsync(key);
        }

        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task StoreAsync_RejectsInvalidKeysBeforeCallingTheOperatingSystem()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialSecretStore();

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.StoreAsync("invalid/key", "not-a-real-secret"));
    }
}
