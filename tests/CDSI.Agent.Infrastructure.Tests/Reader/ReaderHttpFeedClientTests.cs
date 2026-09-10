using System.Net;
using System.Net.Http.Headers;
using CDSI.Agent.Core.Reader;
using CDSI.Agent.Infrastructure.Reader;

namespace CDSI.Agent.Infrastructure.Tests.Reader;

public sealed class ReaderHttpFeedClientTests
{
    [Fact]
    public async Task Fetch_SendsConditionalHeadersAndHandlesNotModified()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Headers = { ETag = new EntityTagHeaderValue("\"new\"") }
        });
        using var httpClient = new HttpClient(handler);
        var client = new ReaderHttpFeedClient(httpClient, new SyndicationFeedParser());

        var result = await client.FetchAsync(new ReaderFeedFetchRequest(
            new Uri("http://127.0.0.1/feed.xml"),
            "\"old\"",
            "Tue, 01 Sep 2026 12:00:00 GMT",
            AllowPrivateNetwork: true));

        Assert.True(result.NotModified);
        Assert.Equal("\"new\"", result.ETag);
        Assert.Equal("\"old\"", handler.LastRequest!.Headers.IfNoneMatch.Single().ToString());
        Assert.NotNull(handler.LastRequest.Headers.IfModifiedSince);
    }

    [Fact]
    public async Task Fetch_ValidatesRedirectSchemeBeforeFollowingIt()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("file:///c:/windows/win.ini") }
        });
        using var httpClient = new HttpClient(handler);
        var client = new ReaderHttpFeedClient(httpClient, new SyndicationFeedParser());

        await Assert.ThrowsAsync<ArgumentException>(() => client.FetchAsync(
            new ReaderFeedFetchRequest(
                new Uri("http://127.0.0.1/feed.xml"),
                null,
                null,
                AllowPrivateNetwork: true)));
    }

    [Fact]
    public async Task Fetch_RejectsOversizedResponseBeforeReadingBody()
    {
        var handler = new RecordingHandler(_ =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 10 * 1024 * 1024 + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var httpClient = new HttpClient(handler);
        var client = new ReaderHttpFeedClient(httpClient, new SyndicationFeedParser());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(
            new ReaderFeedFetchRequest(
                new Uri("http://127.0.0.1/feed.xml"),
                null,
                null,
                AllowPrivateNetwork: true)));
    }

    [Fact]
    public async Task Fetch_ResolvesRelativeEntryLinksAgainstFinalFeedUrl()
    {
        const string rss = """
            <rss version="2.0"><channel><title>Source</title>
              <item><guid>one</guid><title>Entry</title><link>../articles/one</link></item>
            </channel></rss>
            """;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rss)
        });
        using var httpClient = new HttpClient(handler);
        var client = new ReaderHttpFeedClient(httpClient, new SyndicationFeedParser());

        var result = await client.FetchAsync(new ReaderFeedFetchRequest(
            new Uri("http://127.0.0.1/feeds/main.xml"),
            null,
            null,
            AllowPrivateNetwork: true));

        Assert.Equal(
            "http://127.0.0.1/articles/one",
            Assert.Single(result.ParsedFeed!.Entries).Url);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }
    }
}
