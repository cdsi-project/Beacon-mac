using CDSI.Agent.Core.Reader;
using CDSI.Agent.Infrastructure.Reader;

namespace CDSI.Agent.Infrastructure.Tests.Reader;

public sealed class SqliteReaderRepositoryTests
{
    [Fact]
    public async Task Repository_PersistsEntriesStateSearchAndSnapshotRoundTrip()
    {
        using var directory = new TestDirectory();
        using var repository = new SqliteReaderRepository(Path.Combine(directory.Path, "reader.db"));
        await repository.InitializeAsync();
        var feed = CreateFeed();
        var entry = CreateEntry(feed.Id);
        var log = CreateLog(feed.Id);

        var inserted = await repository.SaveFetchedFeedAsync(feed, [entry], log);
        var insertedAgain = await repository.SaveFetchedFeedAsync(
            feed,
            [entry with { Id = Guid.NewGuid(), Title = "Updated title" }],
            CreateLog(feed.Id));
        await repository.SetEntryReadAsync(entry.Id, true, DateTimeOffset.UtcNow);
        await repository.SetEntryStarredAsync(entry.Id, true, DateTimeOffset.UtcNow);

        Assert.Equal(1, inserted);
        Assert.Equal(0, insertedAgain);
        var feedSummary = Assert.Single(await repository.ListFeedsAsync());
        Assert.Equal(1, feedSummary.EntryCount);
        Assert.Equal(0, feedSummary.UnreadCount);
        var searched = Assert.Single(await repository.ListEntriesAsync(
            new ReaderEntryQuery(SearchText: "Updated")));
        Assert.True(searched.Entry.IsRead);
        Assert.True(searched.Entry.IsStarred);

        var snapshot = await repository.CreateSnapshotAsync();
        using var restored = new SqliteReaderRepository(Path.Combine(directory.Path, "restored.db"));
        await restored.InitializeAsync();
        var result = await restored.ImportSnapshotAsync(snapshot);

        Assert.Equal(1, result.FeedsImported);
        Assert.Equal(1, result.EntriesImported);
        Assert.Equal(1, result.StatesImported);
        var restoredEntry = Assert.Single(await restored.ListEntriesAsync(new ReaderEntryQuery()));
        Assert.Equal("Updated title", restoredEntry.Entry.Title);
        Assert.True(restoredEntry.Entry.IsRead);
        Assert.True(restoredEntry.Entry.IsStarred);
    }

    [Fact]
    public async Task DeleteFeed_CascadesEntriesAndState()
    {
        using var directory = new TestDirectory();
        using var repository = new SqliteReaderRepository(Path.Combine(directory.Path, "reader.db"));
        await repository.InitializeAsync();
        var feed = CreateFeed();
        var entry = CreateEntry(feed.Id);
        await repository.SaveFetchedFeedAsync(feed, [entry], CreateLog(feed.Id));
        await repository.SetEntryStarredAsync(entry.Id, true, DateTimeOffset.UtcNow);

        await repository.DeleteFeedAsync(feed.Id);

        Assert.Empty(await repository.ListFeedsAsync());
        Assert.Empty(await repository.ListEntriesAsync(new ReaderEntryQuery()));
    }

    private static ReaderFeed CreateFeed()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        return new ReaderFeed(
            Guid.NewGuid(),
            "Source",
            "https://example.com/feed.xml",
            "https://example.com/",
            "Description",
            ReaderFeedType.Rss20,
            "Tech",
            "\"etag\"",
            "Tue, 01 Sep 2026 12:00:00 GMT",
            now,
            now,
            now,
            now,
            true,
            false,
            null);
    }

    private static ReaderEntry CreateEntry(Guid feedId)
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        return new ReaderEntry(
            Guid.NewGuid(),
            feedId,
            "id:one",
            "one",
            "Original title",
            "https://example.com/one",
            "Alice",
            "Summary text",
            "Content text",
            now,
            null,
            now,
            false,
            false,
            null,
            null);
    }

    private static ReaderFetchLog CreateLog(Guid feedId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ReaderFetchLog(
            Guid.NewGuid(),
            feedId,
            now,
            now,
            200,
            "Success",
            0,
            100,
            10,
            null);
    }
}
