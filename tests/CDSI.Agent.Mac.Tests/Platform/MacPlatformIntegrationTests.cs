using CDSI.Agent.Mac.Platform;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Platform;

public sealed class MacPlatformIntegrationTests
{
    [Fact]
    public void RevealInFinderStartInfoUsesStructuredArguments()
    {
        var path = Path.Combine(Path.GetTempPath(), "Creator Assets", "clip.mp4");

        var startInfo = MacPlatformIntegration.CreateRevealInFinderStartInfo(path);

        Assert.Equal("/usr/bin/open", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            ["-R", Path.GetFullPath(path)],
            startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void OpenUrlStartInfoAllowsHttpsAndUsesStructuredArguments()
    {
        var startInfo = MacPlatformIntegration.CreateOpenUrlStartInfo(
            "https://example.com/projects?q=Beacon");

        Assert.Equal("/usr/bin/open", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            ["https://example.com/projects?q=Beacon"],
            startInfo.ArgumentList.ToArray());
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("file:///tmp/private.txt")]
    [InlineData("javascript:alert(1)")]
    public void OpenUrlStartInfoRejectsNonWebUrls(string url)
    {
        Assert.Throws<ArgumentException>(() =>
            MacPlatformIntegration.CreateOpenUrlStartInfo(url));
    }

    [Fact]
    public void InstanceScopeIsStablePerApplicationAndUser()
    {
        var first = MacSingleInstanceCoordinator.CreateInstanceScope(
            "com.cdsi.beacon",
            "test-user");
        var second = MacSingleInstanceCoordinator.CreateInstanceScope(
            "com.cdsi.beacon",
            "test-user");
        var otherUser = MacSingleInstanceCoordinator.CreateInstanceScope(
            "com.cdsi.beacon",
            "other-user");

        Assert.Equal(first, second);
        Assert.NotEqual(first, otherUser);
        Assert.Matches("^cdsi-beacon-[0-9a-f]{32}$", first);
    }

    [Fact]
    public void ActivationPipeNameFitsTheMacOsUnixSocketLimit()
    {
        var pipeName = MacSingleInstanceCoordinator.CreateActivationPipeName(
            "com.cdsi.beacon",
            "test-user");
        var representativeSocketPath = Path.Combine(
            "/var/folders/zz/abcdefghijklmnopqrstuvwxyz0123456789/T",
            $"CoreFxPipe_{pipeName}");

        Assert.Matches("^c[0-9a-f]{24}$", pipeName);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(representativeSocketPath) <= 104,
            representativeSocketPath);
    }
}
