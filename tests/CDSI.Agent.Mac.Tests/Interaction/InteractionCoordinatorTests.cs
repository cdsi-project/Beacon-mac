using CDSI.Agent.Mac.Interaction;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Interaction;

public sealed class InteractionCoordinatorTests
{
    [Theory]
    [InlineData(
        "https://github.com/cdsi-project/Beacon.git",
        "https://github.com/cdsi-project/Beacon")]
    [InlineData(
        "HTTPS://GITEE.COM/cdsi/beacon.git",
        "https://gitee.com/cdsi/beacon")]
    [InlineData(
        "git@github.com:cdsi-project/Beacon.git",
        "https://github.com/cdsi-project/Beacon")]
    [InlineData(
        "git@gitee.com:cdsi/beacon.git",
        "https://gitee.com/cdsi/beacon")]
    [InlineData(
        "ssh://git@github.com/cdsi-project/Beacon.git",
        "https://github.com/cdsi-project/Beacon")]
    [InlineData(
        "ssh://git@gitee.com/cdsi/beacon.git",
        "https://gitee.com/cdsi/beacon")]
    public void TryCreateGitRepositoryBrowserUrl_ConvertsSupportedAddresses(
        string repositoryUrl,
        string expectedUrl)
    {
        var converted = InteractionCoordinator.TryCreateGitRepositoryBrowserUrl(
            repositoryUrl,
            out var browserUrl);

        Assert.True(converted);
        Assert.Equal(expectedUrl, browserUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://gitlab.com/cdsi/beacon.git")]
    [InlineData("http://github.com/cdsi/beacon.git")]
    [InlineData("https://token@github.com/cdsi/beacon.git")]
    [InlineData("https://github.com/cdsi/beacon.git?token=secret")]
    [InlineData("https://github.com/cdsi/beacon.git#readme")]
    [InlineData("git@github.com:../beacon.git")]
    [InlineData("git@gitee.com:beacon.git")]
    [InlineData("ssh://root@github.com/cdsi-project/Beacon.git")]
    public void TryCreateGitRepositoryBrowserUrl_RejectsUnsafeOrUnsupportedAddresses(
        string? repositoryUrl)
    {
        var converted = InteractionCoordinator.TryCreateGitRepositoryBrowserUrl(
            repositoryUrl,
            out var browserUrl);

        Assert.False(converted);
        Assert.Empty(browserUrl);
    }

    [Fact]
    public void FindUnavailableExpectedRestoreAssetsRequiresCoverageForEverySelection()
    {
        var availableId = Guid.NewGuid();
        var unavailableId = Guid.NewGuid();
        var expected = new Dictionary<Guid, string>
        {
            [availableId] = "available.mov",
            [unavailableId] = "missing.mov"
        };

        var unavailable = InteractionCoordinator.FindUnavailableExpectedRestoreAssets(
            expected,
            [availableId, availableId, Guid.NewGuid()]);

        Assert.Equal(["missing.mov"], unavailable);
    }

    [Fact]
    public void FindUnavailableExpectedRestoreAssetsReturnsEmptyForCompleteCoverage()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var expected = new Dictionary<Guid, string>
        {
            [firstId] = "first.mov",
            [secondId] = "second.mov"
        };

        var unavailable = InteractionCoordinator.FindUnavailableExpectedRestoreAssets(
            expected,
            [secondId, firstId]);

        Assert.Empty(unavailable);
    }

    [Fact]
    public void FindLegalFileFindsFilesInMacApplicationBundleResources()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "cdsi-mac-legal-tests",
            Guid.NewGuid().ToString("N"));
        var executableDirectory = Path.Combine(testRoot, "Contents", "MacOS");
        var legalDirectory = Path.Combine(testRoot, "Contents", "Resources", "Legal");
        var expectedPath = Path.Combine(legalDirectory, "README.md");

        try
        {
            Directory.CreateDirectory(executableDirectory);
            Directory.CreateDirectory(legalDirectory);
            File.WriteAllText(expectedPath, "CDSI Beacon for macOS");

            var actualPath = InteractionCoordinator.FindLegalFile(
                executableDirectory,
                "README.md");

            Assert.Equal(expectedPath, actualPath);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
