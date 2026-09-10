using CDSI.Agent.Application.Reader;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Reader;
using CDSI.Agent.Infrastructure.Reader;

namespace CDSI.Agent.IntegrationTests.Reader;

public sealed class ReaderApplicationServiceTests
{
    [Fact]
    public async Task SubscribeRefreshAndDataExport_PreservePortableReaderState()
    {
        using var directory = new TestDirectory();
        using var firstRepository = new SqliteReaderRepository(
            Path.Combine(directory.Path, "reader.db"));
        var client = new StubFeedClient(
            CreateFetchResult(),
            new ReaderFeedFetchResult(
                new Uri("https://example.com/feed.xml"),
                true,
                304,
                "\"v1\"",
                "Tue, 01 Sep 2026 12:00:00 GMT",
                0,
                null));
        var service = new ReaderApplicationService(
            firstRepository,
            client,
            new OpmlSubscriptionExchange());
        await service.InitializeAsync();

        var feed = await service.SubscribeAsync(
            new ReaderSubscribeRequest("https://example.com/feed.xml", FolderName: "Tech"));
        var initialEntry = Assert.Single(
            await service.ListEntriesAsync(new ReaderEntryQuery()));
        await service.SetEntryReadAsync(initialEntry.Entry.Id, true);
        await service.SetEntryStarredAsync(initialEntry.Entry.Id, true);
        var refresh = await service.RefreshFeedAsync(feed.Id);
        var exportPath = Path.Combine(directory.Path, "reader-export.json");
        await service.ExportDataAsync(exportPath);

        Assert.True(refresh.NotModified);
        Assert.Equal("\"v1\"", client.Requests[1].ETag);
        Assert.True(File.Exists(exportPath));

        using var restoredRepository = new SqliteReaderRepository(
            Path.Combine(directory.Path, "restored.db"));
        var restoredService = new ReaderApplicationService(
            restoredRepository,
            new StubFeedClient(),
            new OpmlSubscriptionExchange());
        await restoredService.InitializeAsync();
        var import = await restoredService.ImportDataAsync(exportPath);
        var restoredEntry = Assert.Single(
            await restoredService.ListEntriesAsync(new ReaderEntryQuery()));

        Assert.Equal(1, import.FeedsImported);
        Assert.True(restoredEntry.Entry.IsRead);
        Assert.True(restoredEntry.Entry.IsStarred);
    }

    [Fact]
    public async Task Refresh_PreservesPreferredSubscriptionTitle()
    {
        using var directory = new TestDirectory();
        using var repository = new SqliteReaderRepository(Path.Combine(directory.Path, "reader.db"));
        var changed = CreateFetchResult() with
        {
            ParsedFeed = CreateFetchResult().ParsedFeed! with { Title = "Changed source title" }
        };
        var service = new ReaderApplicationService(
            repository,
            new StubFeedClient(CreateFetchResult(), changed),
            new OpmlSubscriptionExchange());
        await service.InitializeAsync();
        var feed = await service.SubscribeAsync(new ReaderSubscribeRequest(
            "https://example.com/feed.xml",
            PreferredTitle: "My source"));

        await service.RefreshFeedAsync(feed.Id);

        Assert.Equal("My source", Assert.Single(await service.ListFeedsAsync()).Feed.Title);
    }

    [Fact]
    public async Task RefreshFailure_LeavesExistingEntriesAndRecordsFeedError()
    {
        using var directory = new TestDirectory();
        using var repository = new SqliteReaderRepository(Path.Combine(directory.Path, "reader.db"));
        var client = new StubFeedClient(CreateFetchResult());
        var service = new ReaderApplicationService(
            repository,
            client,
            new OpmlSubscriptionExchange());
        await service.InitializeAsync();
        var feed = await service.SubscribeAsync(
            new ReaderSubscribeRequest("https://example.com/feed.xml"));
        client.EnqueueFailure(new HttpRequestException("HTTP 503"));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.RefreshFeedAsync(feed.Id));

        Assert.Single(await service.ListEntriesAsync(new ReaderEntryQuery()));
        var failedFeed = Assert.Single(await service.ListFeedsAsync()).Feed;
        Assert.Contains("503", failedFeed.LastError);
    }

    [Fact]
    public async Task ImportData_WithMissingCollections_IsRejected()
    {
        using var directory = new TestDirectory();
        using var repository = new SqliteReaderRepository(Path.Combine(directory.Path, "reader.db"));
        var service = new ReaderApplicationService(
            repository,
            new StubFeedClient(),
            new OpmlSubscriptionExchange());
        await service.InitializeAsync();
        var importPath = Path.Combine(directory.Path, "invalid-reader-data.json");
        await File.WriteAllTextAsync(
            importPath,
            """
            {
              "formatVersion": 1,
              "exportedAt": "2026-09-02T00:00:00Z",
              "feeds": null,
              "entries": []
            }
            """);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ImportDataAsync(importPath));

        Assert.Contains("缺少订阅或条目集合", exception.Message);
    }

    private static ReaderFeedFetchResult CreateFetchResult()
    {
        var published = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        return new ReaderFeedFetchResult(
            new Uri("https://example.com/feed.xml"),
            false,
            200,
            "\"v1\"",
            "Tue, 01 Sep 2026 12:00:00 GMT",
            500,
            new ReaderParsedFeed(
                "Example",
                "https://example.com/",
                "Description",
                ReaderFeedType.Rss20,
                [
                    new ReaderParsedEntry(
                        "one",
                        "Entry one",
                        "https://example.com/one",
                        "Alice",
                        "Summary",
                        "Content",
                        published,
                        null)
                ]));
    }

    private sealed class StubFeedClient : IReaderFeedClient
    {
        private readonly Queue<object> _results;

        public StubFeedClient(params ReaderFeedFetchResult[] results)
        {
            _results = new Queue<object>(results.Cast<object>());
        }

        public List<ReaderFeedFetchRequest> Requests { get; } = [];

        public void EnqueueFailure(Exception exception) => _results.Enqueue(exception);

        public Task<ReaderFeedFetchResult> FetchAsync(
            ReaderFeedFetchRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No stub result is available.");
            }

            var result = _results.Dequeue();
            if (result is Exception exception)
            {
                throw exception;
            }

            return Task.FromResult((ReaderFeedFetchResult)result);
        }
    }
}
