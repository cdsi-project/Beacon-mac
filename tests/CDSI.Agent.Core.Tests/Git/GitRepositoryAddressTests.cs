using CDSI.Agent.Core.Git;

namespace CDSI.Agent.Core.Tests.Git;

public sealed class GitRepositoryAddressTests
{
    [Theory]
    [InlineData(
        GitHostingProvider.GitHub,
        " HTTPS://GitHub.com/cdsi-project/Beacon.git/ ",
        "https://github.com/cdsi-project/Beacon.git")]
    [InlineData(
        GitHostingProvider.GitHub,
        "git@github.com:cdsi-project/Beacon.git",
        "git@github.com:cdsi-project/Beacon.git")]
    [InlineData(
        GitHostingProvider.Gitee,
        "ssh://git@gitee.com/cdsi-project/beacon.git",
        "ssh://git@gitee.com/cdsi-project/beacon.git")]
    public void TryNormalize_AcceptsSupportedHttpsAndSshAddresses(
        GitHostingProvider provider,
        string value,
        string expected)
    {
        var valid = GitRepositoryAddress.TryNormalize(
            provider,
            value,
            out var normalized,
            out var errorMessage);

        Assert.True(valid, errorMessage);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(GitHostingProvider.GitHub, "https://gitee.com/owner/repo.git")]
    [InlineData(GitHostingProvider.Gitee, "https://github.com/owner/repo.git")]
    [InlineData(GitHostingProvider.GitHub, "https://token@github.com/owner/repo.git")]
    [InlineData(GitHostingProvider.GitHub, "http://github.com/owner/repo.git")]
    [InlineData(GitHostingProvider.GitHub, "https://github.com/owner")]
    public void TryNormalize_RejectsMismatchedOrUnsafeAddresses(
        GitHostingProvider provider,
        string value)
    {
        Assert.False(GitRepositoryAddress.TryNormalize(
            provider,
            value,
            out var normalized,
            out var errorMessage));
        Assert.Null(normalized);
        Assert.False(string.IsNullOrWhiteSpace(errorMessage));
    }
}
