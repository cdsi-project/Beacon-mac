using CDSI.Agent.Mac.Platform;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Platform;

public sealed class MacKeychainSecretStoreTests
{
    [Fact]
    public async Task OperationsUseTheBeaconServiceAndKeyAsTheAccount()
    {
        var keychain = new FakeMacKeychainApi();
        var store = new MacKeychainSecretStore(keychain);

        await store.StoreAsync("oss_access_key", "secret-value");
        var exists = await store.ExistsAsync("oss_access_key");
        var retrieved = await store.RetrieveAsync("oss_access_key");
        await store.DeleteAsync("oss_access_key");

        Assert.True(exists);
        Assert.Equal("secret-value", retrieved);
        Assert.All(
            keychain.Calls,
            call => Assert.Equal(MacKeychainSecretStore.ServiceName, call.Service));
        Assert.All(
            keychain.Calls,
            call => Assert.Equal("oss_access_key", call.Account));
        Assert.Equal(["store", "exists", "retrieve", "delete"],
            keychain.Calls.Select(call => call.Operation));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("contains.dot")]
    [InlineData("contains/slash")]
    [InlineData("contains space")]
    public async Task InvalidKeysAreRejectedBeforeCallingTheKeychain(string key)
    {
        var keychain = new FakeMacKeychainApi();
        var store = new MacKeychainSecretStore(keychain);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreAsync(key, "secret"));

        Assert.Empty(keychain.Calls);
    }

    [Fact]
    public async Task CancellationIsObservedBeforeCallingTheKeychain()
    {
        var keychain = new FakeMacKeychainApi();
        var store = new MacKeychainSecretStore(keychain);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.RetrieveAsync("valid_key", cancellation.Token));

        Assert.Empty(keychain.Calls);
    }

    [Fact]
    public async Task MissingSecretIsReturnedAsNullAndDeleteIsIdempotent()
    {
        var store = new MacKeychainSecretStore(new FakeMacKeychainApi());

        Assert.False(await store.ExistsAsync("missing_key"));
        Assert.Null(await store.RetrieveAsync("missing_key"));
        await store.DeleteAsync("missing_key");
    }

    private sealed class FakeMacKeychainApi : IMacKeychainApi
    {
        private readonly Dictionary<(string Service, string Account), string> _secrets = [];

        public List<KeychainCall> Calls { get; } = [];

        public void Store(string service, string account, string secret)
        {
            Calls.Add(new KeychainCall("store", service, account));
            _secrets[(service, account)] = secret;
        }

        public bool Exists(string service, string account)
        {
            Calls.Add(new KeychainCall("exists", service, account));
            return _secrets.ContainsKey((service, account));
        }

        public string? Retrieve(string service, string account)
        {
            Calls.Add(new KeychainCall("retrieve", service, account));
            return _secrets.GetValueOrDefault((service, account));
        }

        public void Delete(string service, string account)
        {
            Calls.Add(new KeychainCall("delete", service, account));
            _secrets.Remove((service, account));
        }
    }

    private sealed record KeychainCall(
        string Operation,
        string Service,
        string Account);
}
