using CDSI.Agent.Infrastructure.Identity;

namespace CDSI.Agent.Infrastructure.Tests.Identity;

public sealed class FileClientIdentityProviderTests
{
    [Fact]
    public void GetOrCreate_CreatesStableCanonicalClientId()
    {
        using var directory = new TestDirectory();

        var first = new FileClientIdentityProvider(directory.Path).GetOrCreate();
        var second = new FileClientIdentityProvider(directory.Path).GetOrCreate();

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.Equal(first, second);
        Assert.Equal(first.Id.ToString("D"), first.Value);
        Assert.True(File.Exists(Path.Combine(
            directory.Path,
            FileClientIdentityProvider.IdentityFileName)));
    }

    [Fact]
    public void GetOrCreate_CreatesDifferentIdsForDifferentInstallations()
    {
        using var firstDirectory = new TestDirectory();
        using var secondDirectory = new TestDirectory();

        var first = new FileClientIdentityProvider(firstDirectory.Path).GetOrCreate();
        var second = new FileClientIdentityProvider(secondDirectory.Path).GetOrCreate();

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void GetOrCreate_RejectsCorruptIdentityWithoutReplacingIt()
    {
        using var directory = new TestDirectory();
        var identityPath = Path.Combine(
            directory.Path,
            FileClientIdentityProvider.IdentityFileName);
        const string corruptContent = "not a client identity";
        File.WriteAllText(identityPath, corruptContent);

        var provider = new FileClientIdentityProvider(directory.Path);

        Assert.Throws<InvalidDataException>(() => provider.GetOrCreate());
        Assert.Equal(corruptContent, File.ReadAllText(identityPath));
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentCreatorsResolveToOneIdentity()
    {
        using var directory = new TestDirectory();
        var providers = Enumerable.Range(0, 8)
            .Select(_ => new FileClientIdentityProvider(directory.Path))
            .ToArray();

        var identities = await Task.WhenAll(
            providers.Select(provider => Task.Run(provider.GetOrCreate)));

        Assert.Single(identities.Select(identity => identity.Id).Distinct());
    }
}
