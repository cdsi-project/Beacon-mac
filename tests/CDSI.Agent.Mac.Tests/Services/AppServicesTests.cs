using CDSI.Agent.Mac.Services;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Services;

public sealed class AppServicesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeDataDirectoryOverrideIgnoresMissingValues(string? value)
    {
        Assert.Null(AppServices.NormalizeDataDirectoryOverride(value));
    }

    [Fact]
    public void NormalizeDataDirectoryOverrideAcceptsAnAbsolutePath()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "cdsi-isolated-data",
            Guid.NewGuid().ToString("N"));

        Assert.Equal(
            Path.GetFullPath(path),
            AppServices.NormalizeDataDirectoryOverride($"  {path}  "));
    }

    [Fact]
    public void NormalizeDataDirectoryOverrideRejectsRelativePaths()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AppServices.NormalizeDataDirectoryOverride("relative/data"));

        Assert.Contains("必须是绝对路径", exception.Message);
    }
}
