using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Reader;

internal static class ReaderDatabaseMigrator
{
    internal const int CurrentSchemaVersion = 1;

    public static async Task MigrateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 10000;

            CREATE TABLE IF NOT EXISTS reader_schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText =
            "SELECT COALESCE(MAX(version), 0) FROM reader_schema_migrations;";
        var version = Convert.ToInt64(
            await versionCommand.ExecuteScalarAsync(cancellationToken));
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"RSS订阅数据库架构版本 {version} 高于当前 Beacon 支持的版本 " +
                $"{CurrentSchemaVersion}，请升级 Beacon 后重试。");
        }

        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var migration = connection.CreateCommand();
        migration.Transaction = (SqliteTransaction)transaction;
        migration.CommandText =
            """
            CREATE TABLE reader_feeds (
                id TEXT NOT NULL PRIMARY KEY,
                title TEXT NOT NULL,
                feed_url TEXT NOT NULL,
                feed_url_key TEXT NOT NULL UNIQUE,
                site_url TEXT NULL,
                description TEXT NULL,
                feed_type TEXT NOT NULL,
                folder_name TEXT NULL,
                etag TEXT NULL,
                last_modified TEXT NULL,
                last_checked_at TEXT NULL,
                last_success_at TEXT NULL,
                last_entry_at TEXT NULL,
                created_at TEXT NOT NULL,
                is_enabled INTEGER NOT NULL CHECK(is_enabled IN (0, 1)),
                allow_private_network INTEGER NOT NULL CHECK(allow_private_network IN (0, 1)),
                last_error TEXT NULL
            );

            CREATE TABLE reader_entries (
                id TEXT NOT NULL PRIMARY KEY,
                feed_id TEXT NOT NULL,
                dedupe_key TEXT NOT NULL,
                external_id TEXT NULL,
                title TEXT NOT NULL,
                url TEXT NULL,
                author TEXT NULL,
                summary TEXT NULL,
                content TEXT NULL,
                published_at TEXT NULL,
                updated_at TEXT NULL,
                fetched_at TEXT NOT NULL,
                FOREIGN KEY (feed_id) REFERENCES reader_feeds(id) ON DELETE CASCADE,
                UNIQUE (feed_id, dedupe_key)
            );

            CREATE TABLE reader_entry_states (
                entry_id TEXT NOT NULL PRIMARY KEY,
                is_read INTEGER NOT NULL DEFAULT 0 CHECK(is_read IN (0, 1)),
                is_starred INTEGER NOT NULL DEFAULT 0 CHECK(is_starred IN (0, 1)),
                read_at TEXT NULL,
                starred_at TEXT NULL,
                FOREIGN KEY (entry_id) REFERENCES reader_entries(id) ON DELETE CASCADE
            );

            CREATE TABLE reader_fetch_logs (
                id TEXT NOT NULL PRIMARY KEY,
                feed_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT NOT NULL,
                http_status INTEGER NULL,
                result TEXT NOT NULL,
                new_entries INTEGER NOT NULL,
                response_bytes INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                error TEXT NULL,
                FOREIGN KEY (feed_id) REFERENCES reader_feeds(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_reader_feeds_folder_title
            ON reader_feeds(folder_name, title);

            CREATE INDEX ix_reader_entries_feed_date
            ON reader_entries(feed_id, published_at DESC, fetched_at DESC);

            CREATE INDEX ix_reader_entries_date
            ON reader_entries(published_at DESC, fetched_at DESC);

            CREATE INDEX ix_reader_fetch_logs_feed_time
            ON reader_fetch_logs(feed_id, started_at DESC);

            INSERT INTO reader_schema_migrations(version, applied_at)
            VALUES (1, $applied_at);
            """;
        migration.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
        await migration.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
