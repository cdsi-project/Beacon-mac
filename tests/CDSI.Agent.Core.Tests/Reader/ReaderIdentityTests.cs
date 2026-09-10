using CDSI.Agent.Core.Reader;

namespace CDSI.Agent.Core.Tests.Reader;

public sealed class ReaderIdentityTests
{
    [Fact]
    public void Url_NormalizesHostDefaultPortAndFragment()
    {
        var uri = ReaderUrl.ParseAndNormalize(" HTTPS://Example.COM:443/feed.xml#latest ");

        Assert.Equal("https://example.com/feed.xml", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("file:///c:/feed.xml")]
    [InlineData("https://user:secret@example.com/feed")]
    [InlineData("not-a-url")]
    public void Url_RejectsUnsafeOrInvalidValues(string value)
    {
        Assert.Throws<ArgumentException>(() => ReaderUrl.ParseAndNormalize(value));
    }

    [Fact]
    public void EntryIdentity_PrefersExternalIdThenCanonicalUrl()
    {
        var withId = CreateEntry("entry-1", "https://example.com/a#part");
        var withUrl = CreateEntry(null, "https://EXAMPLE.com:443/a#part");

        Assert.Equal("id:entry-1", ReaderEntryIdentity.CreateKey(withId));
        Assert.Equal("url:https://example.com/a", ReaderEntryIdentity.CreateKey(withUrl));
    }

    [Fact]
    public void EntryIdentity_HashesVeryLongExternalIds()
    {
        var key = ReaderEntryIdentity.CreateKey(CreateEntry(new string('x', 3_000), null));

        Assert.StartsWith("id-hash:", key, StringComparison.Ordinal);
        Assert.Equal("id-hash:".Length + 64, key.Length);
    }

    private static ReaderParsedEntry CreateEntry(string? id, string? url)
    {
        return new ReaderParsedEntry(
            id,
            "Title",
            url,
            "Author",
            null,
            null,
            DateTimeOffset.Parse("2026-09-01T12:00:00Z"),
            null);
    }
}
