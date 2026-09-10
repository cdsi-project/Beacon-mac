using CDSI.Agent.Core.Reader;
using CDSI.Agent.Infrastructure.Reader;

namespace CDSI.Agent.Infrastructure.Tests.Reader;

public sealed class OpmlSubscriptionExchangeTests
{
    [Fact]
    public void Parse_PreservesNestedFolderAndDoesNotLoseInvalidEntry()
    {
        const string opml = """
            <opml version="2.0">
              <body>
                <outline text="Tech">
                  <outline text="AI">
                    <outline text="Example" xmlUrl="https://example.com/feed.xml" htmlUrl="https://example.com/" />
                  </outline>
                </outline>
                <outline text="Broken" xmlUrl="not-a-url" />
              </body>
            </opml>
            """;
        var exchange = new OpmlSubscriptionExchange();

        var result = exchange.Parse(opml);

        Assert.Equal(2, result.Count);
        Assert.Equal("Tech/AI", result[0].FolderName);
        Assert.Equal("not-a-url", result[1].FeedUrl);
    }

    [Fact]
    public void Serialize_CanBeParsedAgain()
    {
        var feed = new ReaderFeed(
            Guid.NewGuid(),
            "Example",
            "https://example.com/feed.xml",
            "https://example.com/",
            null,
            ReaderFeedType.Rss20,
            "Tech",
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            true,
            false,
            null);
        var exchange = new OpmlSubscriptionExchange();

        var parsed = exchange.Parse(exchange.Serialize([feed]));

        var restored = Assert.Single(parsed);
        Assert.Equal(feed.FeedUrl, restored.FeedUrl);
        Assert.Equal("Tech", restored.FolderName);
    }
}
