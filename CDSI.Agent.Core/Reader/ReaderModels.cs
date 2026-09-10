namespace CDSI.Agent.Core.Reader;

public enum ReaderFeedType
{
    Rss20,
    Atom,
    JsonFeed
}

public sealed record ReaderFeed(
    Guid Id,
    string Title,
    string FeedUrl,
    string? SiteUrl,
    string? Description,
    ReaderFeedType Type,
    string? FolderName,
    string? ETag,
    string? LastModified,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastEntryAt,
    DateTimeOffset CreatedAt,
    bool IsEnabled,
    bool AllowPrivateNetwork,
    string? LastError);

public sealed record ReaderFeedSummary(
    ReaderFeed Feed,
    int EntryCount,
    int UnreadCount);

public sealed record ReaderEntry(
    Guid Id,
    Guid FeedId,
    string DeduplicationKey,
    string? ExternalId,
    string Title,
    string? Url,
    string? Author,
    string? Summary,
    string? Content,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset FetchedAt,
    bool IsRead,
    bool IsStarred,
    DateTimeOffset? ReadAt,
    DateTimeOffset? StarredAt);

public sealed record ReaderEntryListItem(
    ReaderEntry Entry,
    string FeedTitle);

public sealed record ReaderEntryQuery(
    Guid? FeedId = null,
    bool UnreadOnly = false,
    bool StarredOnly = false,
    string? SearchText = null,
    int Limit = 500);

public sealed record ReaderParsedFeed(
    string Title,
    string? SiteUrl,
    string? Description,
    ReaderFeedType Type,
    IReadOnlyList<ReaderParsedEntry> Entries);

public sealed record ReaderParsedEntry(
    string? ExternalId,
    string Title,
    string? Url,
    string? Author,
    string? Summary,
    string? Content,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? UpdatedAt);

public sealed record ReaderFeedFetchRequest(
    Uri FeedUri,
    string? ETag,
    string? LastModified,
    bool AllowPrivateNetwork);

public sealed record ReaderFeedFetchResult(
    Uri FinalUri,
    bool NotModified,
    int HttpStatus,
    string? ETag,
    string? LastModified,
    long ResponseBytes,
    ReaderParsedFeed? ParsedFeed);

public sealed record ReaderFetchLog(
    Guid Id,
    Guid FeedId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int? HttpStatus,
    string Result,
    int NewEntries,
    long ResponseBytes,
    long DurationMilliseconds,
    string? Error);

public sealed record ReaderDataSnapshot(
    int FormatVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<ReaderFeed> Feeds,
    IReadOnlyList<ReaderEntry> Entries);

public sealed record ReaderDataImportResult(
    int FeedsImported,
    int EntriesImported,
    int StatesImported);

public sealed record ReaderSubscriptionDefinition(
    string FeedUrl,
    string? Title,
    string? SiteUrl,
    string? FolderName);

public sealed record ReaderOpmlImportResult(
    int Imported,
    int Skipped,
    int Failed,
    IReadOnlyList<string> Errors);

public sealed record ReaderRefreshProgress(
    int Completed,
    int Total,
    string FeedTitle,
    string? Error);

public sealed record ReaderRefreshSummary(
    int Total,
    int Succeeded,
    int NotModified,
    int Failed,
    int NewEntries,
    IReadOnlyList<string> Errors);
