using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class SqliteScanPolicyMigrationTests
{
    [Fact]
    public async Task Version27ScanRoots_DefaultIdleScanningToDisabled()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                CREATE TABLE scan_roots (
                    id TEXT NOT NULL PRIMARY KEY,
                    path TEXT NOT NULL,
                    path_key TEXT NOT NULL UNIQUE,
                    mode TEXT NOT NULL,
                    enabled INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_scanned_at TEXT NULL,
                    removed_at TEXT NULL,
                    volume_id TEXT NULL,
                    volume_relative_path TEXT NULL,
                    file_type_filter TEXT NOT NULL DEFAULT 'All',
                    extension_whitelist_json TEXT NOT NULL DEFAULT '[]',
                    file_type_filters_json TEXT NOT NULL DEFAULT
                        '["Video","Audio","Image","Document","Other"]'
                );
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (27, $now);
                INSERT INTO scan_roots(
                    id, path, path_key, mode, enabled, status,
                    created_at, updated_at)
                VALUES($id, 'D:\Assets', 'd:\assets', 'Readonly', 1, 'Active',
                    $now, $now);
                """;
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await DatabaseMigrator.MigrateAsync(connectionString, default);
        var repository = new SqliteAssetRepository(databasePath);
        var root = Assert.Single(await repository.ListScanRootsAsync());

        Assert.Equal(IdleScanSchedule.Disabled, root.GetIdleScanSchedule());
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(28, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Version22ScanRoots_MigrateWithoutChangingLegacyBehavior()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var setupConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();

        await using (var connection = new SqliteConnection(setupConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                CREATE TABLE scan_roots (
                    id TEXT NOT NULL PRIMARY KEY,
                    path TEXT NOT NULL,
                    path_key TEXT NOT NULL UNIQUE,
                    mode TEXT NOT NULL,
                    enabled INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_scanned_at TEXT NULL,
                    removed_at TEXT NULL,
                    volume_id TEXT NULL,
                    volume_relative_path TEXT NULL,
                    file_type_filter TEXT NOT NULL DEFAULT 'All',
                    extension_whitelist_json TEXT NOT NULL DEFAULT '[]'
                );
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (22, $now);
                INSERT INTO scan_roots(
                    id, path, path_key, mode, enabled, status,
                    created_at, updated_at, file_type_filter,
                    extension_whitelist_json)
                VALUES
                    ($all_id, 'D:\All', 'd:\all', 'Readonly', 1, 'Active',
                     $now, $now, 'All', '[]'),
                    ($video_id, 'D:\Video', 'd:\video', 'Readonly', 1, 'Active',
                     $now, $now, 'Video', '[]'),
                    ($custom_id, 'D:\Custom', 'd:\custom', 'Readonly', 1, 'Active',
                     $now, $now, 'All', '[".psd"]');
                """;
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$all_id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$video_id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$custom_id", Guid.NewGuid().ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await DatabaseMigrator.MigrateAsync(setupConnectionString, default);

        var migratedPolicies = new Dictionary<string, string>();
        await using (var connection = new SqliteConnection(setupConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT path, file_type_filters_json
                FROM scan_roots;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                migratedPolicies.Add(reader.GetString(0), reader.GetString(1));
            }
        }

        Assert.Equal(
            "[\"Video\",\"Audio\",\"Image\",\"Document\",\"Other\"]",
            migratedPolicies["D:\\All"]);
        Assert.Equal("[\"Video\"]", migratedPolicies["D:\\Video"]);
        Assert.Equal("[]", migratedPolicies["D:\\Custom"]);
    }
}
