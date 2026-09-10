using CDSI.Agent.Core.OpenWeb;

namespace CDSI.Agent.Core.Tests.OpenWeb;

public sealed class OpenWebOriginDomainTests
{
    [Theory]
    [InlineData("WWW.Example.COM.", "www.example.com")]
    [InlineData("münich.example", "xn--mnich-kva.example")]
    [InlineData("origin.internal", "origin.internal")]
    public void TryNormalize_ReturnsACanonicalDomain(
        string input,
        string expected)
    {
        var success = OpenWebOriginDomain.TryNormalize(
            input,
            out var normalizedDomain,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.Equal(expected, normalizedDomain);
    }

    [Fact]
    public void TryNormalize_AllowsAnEmptyValueToClearTheSetting()
    {
        var success = OpenWebOriginDomain.TryNormalize(
            "   ",
            out var normalizedDomain,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(normalizedDomain);
        Assert.Null(errorMessage);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com:443")]
    [InlineData("user@example.com")]
    [InlineData("127.0.0.1")]
    [InlineData("-bad.example")]
    [InlineData("bad_.example")]
    public void TryNormalize_RejectsValuesThatAreNotDomainNames(string input)
    {
        var success = OpenWebOriginDomain.TryNormalize(
            input,
            out var normalizedDomain,
            out var errorMessage);

        Assert.False(success);
        Assert.Null(normalizedDomain);
        Assert.False(string.IsNullOrWhiteSpace(errorMessage));
    }
}
