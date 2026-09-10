using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Reader;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Reader;

public sealed partial class SqliteReaderRepository : IReaderRepository, IDisposable
{
    private readonly string _connectionString;

    public SqliteReaderRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var normalized = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(normalized)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = normalized,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return ReaderDatabaseMigrator.MigrateAsync(_connectionString, cancellationToken);
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public async Task<IReadOnlyList<ReaderFeedSummary>> ListFeedsAsync(
        CancellationToken cancellationToken = default)
    {
        var feeds = new List<ReaderFeedSummary>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.id, f.title, f.feed_url, f.site_url, f.description,
                   f.feed_type, f.folder_name, f.etag, f.last_modified,
                   f.last_checked_at, f.last_success_at, f.last_entry_at,
                   f.created_at, f.is_enabled, f.allow_private_network, f.last_error,
                   COUNT(e.id) AS entry_count,
                   SUM(CASE WHEN e.id IS NOT NULL AND COALESCE(s.is_read, 0) = 0 THEN 1 ELSE 0 END)
                       AS unread_count
            FROM reader_feeds f
            LEFT JOIN reader_entries e ON e.feed_id = f.id
            LEFT JOIN reader_entry_states s ON s.entry_id = e.id
            GROUP BY f.id
            ORDER BY COALESCE(f.folder_name, ''), f.title COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            feeds.Add(new ReaderFeedSummary(
                ReadFeed(reader),
                reader.GetInt32(16),
                reader.GetInt32(17)));
        }

        return feeds;
    }

    public async Task<ReaderFeed?> GetFeedAsync(
        Guid feedId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {FeedColumns} FROM reader_feeds WHERE id = $id;";
        command.Parameters.AddWithValue("$id", feedId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFeed(reader) : null;
    }

    public async Task<ReaderFeed?> FindFeedByUrlAsync(
        string feedUrl,
        CancellationToken cancellationToken = default)
    {
        var key = ReaderUrl.CreateKey(feedUrl);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {FeedColumns} FROM reader_feeds WHERE feed_url_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFeed(reader) : null;
    }

    public async Task<int> SaveFetchedFeedAsync(
        ReaderFeed feed,
        IReadOnlyCollection<ReaderEntry> entries,
        ReaderFetchLog fetchLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(fetchLog);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertFeedAsync(connection, (SqliteTransaction)transaction, feed, cancellationToken);
        var inserted = 0;
        foreach (var entry in entries)
        {
            inserted += await UpsertEntryAsync(
                connection,
                (SqliteTransaction)transaction,
                entry,
                cancellationToken);
        }

        await InsertFetchLogAsync(
            connection,
            (SqliteTransaction)transaction,
            fetchLog with { NewEntries = inserted },
            cancellationToken);
        await PruneFetchLogsAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task SaveFetchFailureAsync(
        Guid feedId,
        DateTimeOffset checkedAt,
        string error,
        ReaderFetchLog fetchLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE reader_feeds
                SET last_checked_at = $checked_at,
                    last_error = $error
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$checked_at", checkedAt.ToString("O"));
            update.Parameters.AddWithValue("$error", Truncate(error, 2_000));
            update.Parameters.AddWithValue("$id", feedId.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertFetchLogAsync(
            connection,
            (SqliteTransaction)transaction,
            fetchLog with { Error = Truncate(fetchLog.Error, 2_000) },
            cancellationToken);
        await PruneFetchLogsAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteFeedAsync(
        Guid feedId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reader_feeds WHERE id = $id;";
        command.Parameters.AddWithValue("$id", feedId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task UpsertFeedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReaderFeed feed,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO reader_feeds(
                id, title, feed_url, feed_url_key, site_url, description,
                feed_type, folder_name, etag, last_modified, last_checked_at,
                last_success_at, last_entry_at, created_at, is_enabled,
                allow_private_network, last_error)
            VALUES(
                $id, $title, $feed_url, $feed_url_key, $site_url, $description,
                $feed_type, $folder_name, $etag, $last_modified, $last_checked_at,
                $last_success_at, $last_entry_at, $created_at, $is_enabled,
                $allow_private_network, $last_error)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                feed_url = excluded.feed_url,
                feed_url_key = excluded.feed_url_key,
                site_url = excluded.site_url,
                description = excluded.description,
                feed_type = excluded.feed_type,
                folder_name = excluded.folder_name,
                etag = excluded.etag,
                last_modified = excluded.last_modified,
                last_checked_at = excluded.last_checked_at,
                last_success_at = excluded.last_success_at,
                last_entry_at = excluded.last_entry_at,
                is_enabled = excluded.is_enabled,
                allow_private_network = excluded.allow_private_network,
                last_error = excluded.last_error;
            """;
        AddFeedParameters(command, feed);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> UpsertEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReaderEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO reader_entries(
                id, feed_id, dedupe_key, external_id, title, url, author,
                summary, content, published_at, updated_at, fetched_at)
            VALUES(
                $id, $feed_id, $dedupe_key, $external_id, $title, $url, $author,
                $summary, $content, $published_at, $updated_at, $fetched_at)
            ON CONFLICT(feed_id, dedupe_key) DO NOTHING;
            """;
        AddEntryParameters(command, entry);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (inserted > 0)
        {
            return inserted;
        }

        command.Parameters.Clear();
        command.CommandText =
            """
            UPDATE reader_entries
            SET external_id = $external_id,
                title = $title,
                url = $url,
                author = $author,
                summary = $summary,
                content = $content,
                published_at = $published_at,
                updated_at = $updated_at,
                fetched_at = $fetched_at
            WHERE feed_id = $feed_id AND dedupe_key = $dedupe_key;
            """;
        AddEntryParameters(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return 0;
    }

    private static async Task InsertFetchLogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReaderFetchLog log,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO reader_fetch_logs(
                id, feed_id, started_at, finished_at, http_status, result,
                new_entries, response_bytes, duration_ms, error)
            VALUES(
                $id, $feed_id, $started_at, $finished_at, $http_status, $result,
                $new_entries, $response_bytes, $duration_ms, $error);
            """;
        command.Parameters.AddWithValue("$id", log.Id.ToString("D"));
        command.Parameters.AddWithValue("$feed_id", log.FeedId.ToString("D"));
        command.Parameters.AddWithValue("$started_at", log.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$finished_at", log.FinishedAt.ToString("O"));
        command.Parameters.AddWithValue("$http_status", DbValue(log.HttpStatus));
        command.Parameters.AddWithValue("$result", log.Result);
        command.Parameters.AddWithValue("$new_entries", log.NewEntries);
        command.Parameters.AddWithValue("$response_bytes", log.ResponseBytes);
        command.Parameters.AddWithValue("$duration_ms", log.DurationMilliseconds);
        command.Parameters.AddWithValue("$error", DbValue(Truncate(log.Error, 2_000)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PruneFetchLogsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM reader_fetch_logs
            WHERE started_at < $cutoff
               OR id IN (
                   SELECT id
                   FROM reader_fetch_logs
                   ORDER BY started_at DESC
                   LIMIT -1 OFFSET 5000
               );
            """;
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddFeedParameters(SqliteCommand command, ReaderFeed feed)
    {
        command.Parameters.AddWithValue("$id", feed.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", Truncate(feed.Title, 1_000) ?? "（无标题）");
        command.Parameters.AddWithValue("$feed_url", feed.FeedUrl);
        command.Parameters.AddWithValue("$feed_url_key", ReaderUrl.CreateKey(feed.FeedUrl));
        command.Parameters.AddWithValue("$site_url", DbValue(Truncate(feed.SiteUrl, 4_000)));
        command.Parameters.AddWithValue("$description", DbValue(Truncate(feed.Description, 20_000)));
        command.Parameters.AddWithValue("$feed_type", feed.Type.ToString());
        command.Parameters.AddWithValue("$folder_name", DbValue(Truncate(feed.FolderName, 500)));
        command.Parameters.AddWithValue("$etag", DbValue(Truncate(feed.ETag, 1_000)));
        command.Parameters.AddWithValue("$last_modified", DbValue(Truncate(feed.LastModified, 200)));
        command.Parameters.AddWithValue("$last_checked_at", DbValue(Format(feed.LastCheckedAt)));
        command.Parameters.AddWithValue("$last_success_at", DbValue(Format(feed.LastSuccessAt)));
        command.Parameters.AddWithValue("$last_entry_at", DbValue(Format(feed.LastEntryAt)));
        command.Parameters.AddWithValue("$created_at", feed.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$is_enabled", feed.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$allow_private_network", feed.AllowPrivateNetwork ? 1 : 0);
        command.Parameters.AddWithValue("$last_error", DbValue(Truncate(feed.LastError, 2_000)));
    }

    private static void AddEntryParameters(SqliteCommand command, ReaderEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$feed_id", entry.FeedId.ToString("D"));
        command.Parameters.AddWithValue("$dedupe_key", Truncate(entry.DeduplicationKey, 4_000)!);
        command.Parameters.AddWithValue("$external_id", DbValue(Truncate(entry.ExternalId, 4_000)));
        command.Parameters.AddWithValue("$title", Truncate(entry.Title, 2_000) ?? "（无标题）");
        command.Parameters.AddWithValue("$url", DbValue(Truncate(entry.Url, 4_000)));
        command.Parameters.AddWithValue("$author", DbValue(Truncate(entry.Author, 1_000)));
        command.Parameters.AddWithValue("$summary", DbValue(Truncate(entry.Summary, 100_000)));
        command.Parameters.AddWithValue("$content", DbValue(Truncate(entry.Content, 1_000_000)));
        command.Parameters.AddWithValue("$published_at", DbValue(Format(entry.PublishedAt)));
        command.Parameters.AddWithValue("$updated_at", DbValue(Format(entry.UpdatedAt)));
        command.Parameters.AddWithValue("$fetched_at", entry.FetchedAt.ToString("O"));
    }

    private static ReaderFeed ReadFeed(SqliteDataReader reader)
    {
        return new ReaderFeed(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            NullableString(reader, 3),
            NullableString(reader, 4),
            Enum.Parse<ReaderFeedType>(reader.GetString(5)),
            NullableString(reader, 6),
            NullableString(reader, 7),
            NullableString(reader, 8),
            NullableDate(reader, 9),
            NullableDate(reader, 10),
            NullableDate(reader, 11),
            DateTimeOffset.Parse(reader.GetString(12)),
            reader.GetInt32(13) != 0,
            reader.GetInt32(14) != 0,
            NullableString(reader, 15));
    }

    private const string FeedColumns =
        "id, title, feed_url, site_url, description, feed_type, folder_name, " +
        "etag, last_modified, last_checked_at, last_success_at, last_entry_at, " +
        "created_at, is_enabled, allow_private_network, last_error";

    private static string? NullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal));
    }

    private static string? Format(DateTimeOffset? value) => value?.ToString("O");

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static string? Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}
