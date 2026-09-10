using CDSI.Agent.Core.Reader;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Reader;

public sealed partial class SqliteReaderRepository
{
    public async Task<ReaderDataSnapshot> CreateSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var feeds = (await ListFeedsAsync(cancellationToken))
            .Select(summary => summary.Feed)
            .ToArray();
        var entries = await ListAllEntriesAsync(cancellationToken);
        return new ReaderDataSnapshot(1, DateTimeOffset.UtcNow, feeds, entries);
    }

    public async Task<ReaderDataImportResult> ImportSnapshotAsync(
        ReaderDataSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.FormatVersion != 1)
        {
            throw new InvalidDataException($"不支持的 Reader 数据格式版本: {snapshot.FormatVersion}");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var feedMap = new Dictionary<Guid, Guid>();
        var feedsImported = 0;
        foreach (var sourceFeed in snapshot.Feeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingId = await FindFeedIdByUrlAsync(
                connection,
                (SqliteTransaction)transaction,
                sourceFeed.FeedUrl,
                cancellationToken);
            var targetId = existingId ?? sourceFeed.Id;
            if (existingId is null && await IdExistsAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    "reader_feeds",
                    targetId,
                    cancellationToken))
            {
                targetId = Guid.NewGuid();
            }

            feedMap[sourceFeed.Id] = targetId;
            await UpsertFeedAsync(
                connection,
                (SqliteTransaction)transaction,
                sourceFeed with { Id = targetId },
                cancellationToken);
            feedsImported++;
        }

        var entriesImported = 0;
        var statesImported = 0;
        foreach (var sourceEntry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!feedMap.TryGetValue(sourceEntry.FeedId, out var targetFeedId))
            {
                continue;
            }

            var existingId = await FindEntryIdAsync(
                connection,
                (SqliteTransaction)transaction,
                targetFeedId,
                sourceEntry.DeduplicationKey,
                cancellationToken);
            var targetId = existingId ?? sourceEntry.Id;
            if (existingId is null && await IdExistsAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    "reader_entries",
                    targetId,
                    cancellationToken))
            {
                targetId = Guid.NewGuid();
            }

            var mapped = sourceEntry with { Id = targetId, FeedId = targetFeedId };
            await UpsertEntryAsync(
                connection,
                (SqliteTransaction)transaction,
                mapped,
                cancellationToken);
            entriesImported++;
            if (mapped.IsRead || mapped.IsStarred)
            {
                await MergeStateAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    mapped,
                    cancellationToken);
                statesImported++;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReaderDataImportResult(feedsImported, entriesImported, statesImported);
    }

    private async Task<IReadOnlyList<ReaderEntry>> ListAllEntriesAsync(
        CancellationToken cancellationToken)
    {
        var entries = new List<ReaderEntry>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {EntryColumns}
            FROM reader_entries e
            LEFT JOIN reader_entry_states s ON s.entry_id = e.id
            ORDER BY e.feed_id, e.fetched_at;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new ReaderEntry(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                NullableString(reader, 3),
                reader.GetString(4),
                NullableString(reader, 5),
                NullableString(reader, 6),
                NullableString(reader, 7),
                NullableString(reader, 8),
                NullableDate(reader, 9),
                NullableDate(reader, 10),
                DateTimeOffset.Parse(reader.GetString(11)),
                reader.GetInt32(12) != 0,
                reader.GetInt32(13) != 0,
                NullableDate(reader, 14),
                NullableDate(reader, 15)));
        }

        return entries;
    }

    private static async Task<Guid?> FindFeedIdByUrlAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string feedUrl,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT id FROM reader_feeds WHERE feed_url_key = $key;";
        command.Parameters.AddWithValue("$key", ReaderUrl.CreateKey(feedUrl));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string id ? Guid.Parse(id) : null;
    }

    private static async Task<Guid?> FindEntryIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid feedId,
        string deduplicationKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id
            FROM reader_entries
            WHERE feed_id = $feed_id AND dedupe_key = $dedupe_key;
            """;
        command.Parameters.AddWithValue("$feed_id", feedId.ToString("D"));
        command.Parameters.AddWithValue("$dedupe_key", deduplicationKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string id ? Guid.Parse(id) : null;
    }

    private static async Task<bool> IdExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        Guid id,
        CancellationToken cancellationToken)
    {
        var allowed = tableName is "reader_feeds" or "reader_entries"
            ? tableName
            : throw new ArgumentOutOfRangeException(nameof(tableName));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {allowed} WHERE id = $id);";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task MergeStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReaderEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO reader_entry_states(
                entry_id, is_read, is_starred, read_at, starred_at)
            VALUES($entry_id, $is_read, $is_starred, $read_at, $starred_at)
            ON CONFLICT(entry_id) DO UPDATE SET
                is_read = MAX(reader_entry_states.is_read, excluded.is_read),
                is_starred = MAX(reader_entry_states.is_starred, excluded.is_starred),
                read_at = CASE
                    WHEN reader_entry_states.read_at IS NULL THEN excluded.read_at
                    WHEN excluded.read_at IS NULL THEN reader_entry_states.read_at
                    ELSE MAX(reader_entry_states.read_at, excluded.read_at)
                END,
                starred_at = CASE
                    WHEN reader_entry_states.starred_at IS NULL THEN excluded.starred_at
                    WHEN excluded.starred_at IS NULL THEN reader_entry_states.starred_at
                    ELSE MAX(reader_entry_states.starred_at, excluded.starred_at)
                END;
            """;
        command.Parameters.AddWithValue("$entry_id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$is_read", entry.IsRead ? 1 : 0);
        command.Parameters.AddWithValue("$is_starred", entry.IsStarred ? 1 : 0);
        command.Parameters.AddWithValue("$read_at", DbValue(Format(entry.ReadAt)));
        command.Parameters.AddWithValue("$starred_at", DbValue(Format(entry.StarredAt)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
