using CDSI.Agent.Core.Reader;

namespace CDSI.Agent.Core.Abstractions;

public interface IReaderRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReaderFeedSummary>> ListFeedsAsync(
        CancellationToken cancellationToken = default);

    Task<ReaderFeed?> GetFeedAsync(
        Guid feedId,
        CancellationToken cancellationToken = default);

    Task<ReaderFeed?> FindFeedByUrlAsync(
        string feedUrl,
        CancellationToken cancellationToken = default);

    Task<int> SaveFetchedFeedAsync(
        ReaderFeed feed,
        IReadOnlyCollection<ReaderEntry> entries,
        ReaderFetchLog fetchLog,
        CancellationToken cancellationToken = default);

    Task SaveFetchFailureAsync(
        Guid feedId,
        DateTimeOffset checkedAt,
        string error,
        ReaderFetchLog fetchLog,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReaderEntryListItem>> ListEntriesAsync(
        ReaderEntryQuery query,
        CancellationToken cancellationToken = default);

    Task<ReaderEntryListItem?> GetEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default);

    Task SetEntryReadAsync(
        Guid entryId,
        bool isRead,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);

    Task SetEntryStarredAsync(
        Guid entryId,
        bool isStarred,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default);

    Task DeleteFeedAsync(
        Guid feedId,
        CancellationToken cancellationToken = default);

    Task<ReaderDataSnapshot> CreateSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<ReaderDataImportResult> ImportSnapshotAsync(
        ReaderDataSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
