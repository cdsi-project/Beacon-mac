using System.Net;
using CDSI.Agent.Mac.Services;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Services;

public sealed class ApplicationUpdateCheckerTests
{
    [Fact]
    public async Task CheckAsync_ReadsAndNormalizesTheGitHubVersionFile()
    {
        string? requestedUrl = null;
        string? requestedUserAgent = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestedUrl = request.RequestUri?.AbsoluteUri;
            requestedUserAgent = request.Headers.UserAgent.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("\uFEFF0.2.11\r\n")
            };
        }));
        using var checker = new ApplicationUpdateChecker(httpClient);

        var result = await checker.CheckAsync("0.2.10");

        Assert.Equal(
            "https://raw.githubusercontent.com/cdsi-project/beacon-mac/main/VERSION",
            requestedUrl);
        Assert.Equal(
            "https://github.com/cdsi-project/beacon-mac/releases",
            ApplicationUpdateChecker.ReleasesUrl);
        Assert.Equal("CDSI-Beacon/0.2.10", requestedUserAgent);
        Assert.Equal("0.2.10", result.CurrentVersion);
        Assert.Equal("0.2.11", result.LatestVersion);
        Assert.True(result.IsUpdateAvailable);
    }

    [Theory]
    [InlineData("0.2.10", "0.206", 1)]
    [InlineData("0.206", "0.2.10", -1)]
    [InlineData("0.2.10", "0.2.10", 0)]
    [InlineData("0.2.99", "0.3.10", -1)]
    [InlineData("1.2.99", "1.2.98", 1)]
    public void CompareVersions_UsesTheBeaconVersionSequence(
        string left,
        string right,
        int expectedSign)
    {
        var comparison = ApplicationUpdateChecker.CompareVersions(left, right);

        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("0.2.100")]
    [InlineData("0.207")]
    [InlineData("not-a-version")]
    public void CompareVersions_RejectsInvalidVersions(string value)
    {
        Assert.Throws<FormatException>(() =>
            ApplicationUpdateChecker.CompareVersions(value, "0.2.10"));
    }

    [Fact]
    public async Task CheckAsync_RejectsAnOversizedVersionFile()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('1', 65))
            }));
        using var checker = new ApplicationUpdateChecker(httpClient);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            checker.CheckAsync("0.2.10"));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
