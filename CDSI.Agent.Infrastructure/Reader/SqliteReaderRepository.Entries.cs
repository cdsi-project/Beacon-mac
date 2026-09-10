using CDSI.Agent.Core.Reader;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Reader;

public sealed partial class SqliteReaderRepository
{
    public async Task<IReadOnlyList<ReaderEntryListItem>> ListEntriesAsync(
        ReaderEntryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var conditions = new List<string>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (query.FeedId is not null)
        {
            conditions.Add("e.feed_id = $feed_id");
            command.Parameters.AddWithValue("$feed_id", query.FeedId.Value.ToString("D"));
        }

        if (query.UnreadOnly)
        {
            conditions.Add("COALESCE(s.is_read, 0) = 0");
        }

        if (query.StarredOnly)
        {
            conditions.Add("COALESCE(s.is_starred, 0) = 1");
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            conditions.Add(
                "(e.title LIKE $search ESCAPE '\\' OR " +
                "COALESCE(e.author, '') LIKE $search ESCAPE '\\' OR " +
                "COALESCE(e.summary, '') LIKE $search ESCAPE '\\')");
            command.Parameters.AddWithValue(
                "$search",
                $"%{EscapeLike(query.SearchText.Trim())}%");
        }

        var where = conditions.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", conditions)}";
        command.CommandText =
            $"""
            SELECT {EntryColumns}, f.title
            FROM reader_entries e
            INNER JOIN reader_feeds f ON f.id = e.feed_id
            LEFT JOIN reader_entry_states s ON s.entry_id = e.id
            {where}
            ORDER BY COALESCE(e.published_at, e.updated_at, e.fetched_at) DESC,
                     e.fetched_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 2_000));
        var entries = new List<ReaderEntryListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ReadEntryListItem(reader));
        }

        return entries;
    }

    public async Task<ReaderEntryListItem?> GetEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {EntryColumns}, f.title
            FROM reader_entries e
            INNER JOIN reader_feeds f ON f.id = e.feed_id
            LEFT JOIN reader_entry_states s ON s.entry_id = e.id
            WHERE e.id = $id;
            """;
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadEntryListItem(reader)
            : null;
    }

    public Task SetEntryReadAsync(
        Guid entryId,
        bool isRead,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        return SetEntryStateAsync(
            entryId,
            "is_read",
            "read_at",
            isRead,
            changedAt,
            cancellationToken);
    }

    public Task SetEntryStarredAsync(
        Guid entryId,
        bool isStarred,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        return SetEntryStateAsync(
            entryId,
            "is_starred",
            "starred_at",
            isStarred,
            changedAt,
            cancellationToken);
    }

    private async Task SetEntryStateAsync(
        Guid entryId,
        string stateColumn,
        string timestampColumn,
        bool enabled,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        var otherStateColumn = stateColumn == "is_read" ? "is_starred" : "is_read";
        var otherTimestampColumn = timestampColumn == "read_at" ? "starred_at" : "read_at";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO reader_entry_states(
                entry_id, {stateColumn}, {otherStateColumn},
                {timestampColumn}, {otherTimestampColumn})
            VALUES($entry_id, $enabled, 0, $changed_at, NULL)
            ON CONFLICT(entry_id) DO UPDATE SET
                {stateColumn} = excluded.{stateColumn},
                {timestampColumn} = excluded.{timestampColumn};
            """;
        command.Parameters.AddWithValue("$entry_id", entryId.ToString("D"));
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$changed_at",
            enabled ? changedAt.ToString("O") : DBNull.Value);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            throw new InvalidOperationException("Reader 条目不存在。");
        }
    }

    private static ReaderEntryListItem ReadEntryListItem(SqliteDataReader reader)
    {
        var entry = new ReaderEntry(
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
            NullableDate(reader, 15));
        return new ReaderEntryListItem(entry, reader.GetString(16));
    }

    private const string EntryColumns =
        "e.id, e.feed_id, e.dedupe_key, e.external_id, e.title, e.url, e.author, " +
        "e.summary, e.content, e.published_at, e.updated_at, e.fetched_at, " +
        "COALESCE(s.is_read, 0), COALESCE(s.is_starred, 0), s.read_at, s.starred_at";

    private static string EscapeLike(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
