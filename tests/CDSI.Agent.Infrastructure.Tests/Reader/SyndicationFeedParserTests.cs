using System.Xml;
using CDSI.Agent.Core.Reader;
using CDSI.Agent.Infrastructure.Reader;

namespace CDSI.Agent.Infrastructure.Tests.Reader;

public sealed class SyndicationFeedParserTests
{
    private readonly SyndicationFeedParser _parser = new();

    [Fact]
    public void Parse_Rss20NormalizesEntryAndRemovesMarkup()
    {
        const string rss = """
            <?xml version="1.0"?>
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
              <channel>
                <title>Example RSS</title>
                <link>https://example.com/</link>
                <description>Updates</description>
                <item>
                  <guid>post-1</guid>
                  <title>First post</title>
                  <link>https://example.com/first</link>
                  <pubDate>Tue, 01 Sep 2026 12:00:00 GMT</pubDate>
                  <content:encoded><![CDATA[<p>Hello <strong>Reader</strong></p><script>alert(1)</script>]]></content:encoded>
                </item>
              </channel>
            </rss>
            """;

        var parsed = _parser.Parse(rss, "application/rss+xml");

        Assert.Equal(ReaderFeedType.Rss20, parsed.Type);
        Assert.Equal("Example RSS", parsed.Title);
        var entry = Assert.Single(parsed.Entries);
        Assert.Equal("post-1", entry.ExternalId);
        Assert.Contains("Hello Reader", entry.Content);
        Assert.DoesNotContain("<script", entry.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", entry.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AtomReadsAlternateLinkAndAuthor()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom source</title>
              <link rel="alternate" href="https://example.com/" />
              <entry>
                <id>tag:example.com,2026:1</id>
                <title>Atom entry</title>
                <author><name>Alice</name></author>
                <updated>2026-09-01T12:00:00Z</updated>
                <summary type="html">A &lt;b&gt;summary&lt;/b&gt;</summary>
              </entry>
            </feed>
            """;

        var parsed = _parser.Parse(atom);

        Assert.Equal(ReaderFeedType.Atom, parsed.Type);
        Assert.Equal("https://example.com/", parsed.SiteUrl);
        var entry = Assert.Single(parsed.Entries);
        Assert.Equal("Alice", entry.Author);
        Assert.Equal("A summary", entry.Summary);
    }

    [Fact]
    public void Parse_JsonFeedReadsTextContent()
    {
        const string json = """
            {
              "version": "https://jsonfeed.org/version/1.1",
              "title": "JSON source",
              "home_page_url": "https://example.com/",
              "items": [
                {
                  "id": "one",
                  "url": "https://example.com/one",
                  "title": "JSON entry",
                  "content_html": "<p>JSON <em>content</em></p>",
                  "date_published": "2026-09-01T12:00:00Z"
                }
              ]
            }
            """;

        var parsed = _parser.Parse(json, "application/feed+json");

        Assert.Equal(ReaderFeedType.JsonFeed, parsed.Type);
        Assert.Equal("JSON content", Assert.Single(parsed.Entries).Content);
    }

    [Fact]
    public void Parse_RejectsXmlExternalEntities()
    {
        const string xml = """
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]>
            <rss version="2.0"><channel><title>&xxe;</title></channel></rss>
            """;

        Assert.Throws<XmlException>(() => _parser.Parse(xml));
    }

    [Fact]
    public void Parse_RejectsOrdinaryJsonWithoutJsonFeedVersion()
    {
        Assert.Throws<InvalidDataException>(() =>
            _parser.Parse("{\"title\":\"not a feed\"}", "application/json"));
    }
}
