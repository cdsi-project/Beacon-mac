using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository
{
    public async Task<AssetDirectoryExclusionResult> ExcludeAssetDirectoryAsync(
        string path,
        DateTimeOffset excludedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = NormalizePath(path);
        var pathKey = CreatePathKey(normalizedPath);
        var pathPrefix = CreatePathKey(AppendDirectorySeparator(normalizedPath));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var exclusionCommand = connection.CreateCommand())
        {
            exclusionCommand.Transaction = (SqliteTransaction)transaction;
            exclusionCommand.CommandText =
                """
                INSERT INTO asset_directory_exclusions(
                    path_key, path, path_prefix, excluded_at)
                VALUES ($path_key, $path, $path_prefix, $excluded_at)
                ON CONFLICT(path_key) DO UPDATE SET
                    path = excluded.path,
                    path_prefix = excluded.path_prefix,
                    excluded_at = excluded.excluded_at;
                """;
            exclusionCommand.Parameters.AddWithValue("$path_key", pathKey);
            exclusionCommand.Parameters.AddWithValue("$path", normalizedPath);
            exclusionCommand.Parameters.AddWithValue("$path_prefix", pathPrefix);
            exclusionCommand.Parameters.AddWithValue("$excluded_at", excludedAt.ToString("O"));
            await exclusionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        int excludedLocationCount;
        await using (var locationCommand = connection.CreateCommand())
        {
            locationCommand.Transaction = (SqliteTransaction)transaction;
            locationCommand.CommandText =
                """
                UPDATE asset_locations
                SET excluded_from_asset_list = 1,
                    excluded_from_asset_list_at = $excluded_at
                WHERE location_type = 'Local'
                  AND excluded_from_asset_list = 0
                  AND (
                      path_key = $path_key
                      OR substr(path_key, 1, length($path_prefix)) = $path_prefix
                  );
                """;
            locationCommand.Parameters.AddWithValue("$path_key", pathKey);
            locationCommand.Parameters.AddWithValue("$path_prefix", pathPrefix);
            locationCommand.Parameters.AddWithValue("$excluded_at", excludedAt.ToString("O"));
            excludedLocationCount = await locationCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        int stoppedScanRootCount;
        await using (var rootCommand = connection.CreateCommand())
        {
            rootCommand.Transaction = (SqliteTransaction)transaction;
            rootCommand.CommandText =
                """
                UPDATE scan_roots
                SET enabled = 0,
                    status = CASE
                        WHEN mode = $readonly_mode THEN 'Removed'
                        ELSE 'Disabled'
                    END,
                    updated_at = $excluded_at,
                    removed_at = CASE
                        WHEN mode = $readonly_mode THEN $excluded_at
                        ELSE NULL
                    END
                WHERE removed_at IS NULL
                  AND (
                      path_key = $path_key
                      OR substr(path_key, 1, length($path_prefix)) = $path_prefix
                  );
                """;
            rootCommand.Parameters.AddWithValue("$readonly_mode", ScanRootMode.Readonly.ToString());
            rootCommand.Parameters.AddWithValue("$path_key", pathKey);
            rootCommand.Parameters.AddWithValue("$path_prefix", pathPrefix);
            rootCommand.Parameters.AddWithValue("$excluded_at", excludedAt.ToString("O"));
            stoppedScanRootCount = await rootCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AssetDirectoryExclusionResult(
            normalizedPath,
            excludedLocationCount,
            stoppedScanRootCount);
    }

    public async Task<IReadOnlyList<string>> ListExcludedAssetDirectoryPathsAsync(
        CancellationToken cancellationToken = default)
    {
        var paths = new List<string>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT path
            FROM asset_directory_exclusions
            ORDER BY path_key;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    public async Task RestoreAssetDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = NormalizePath(path);
        var pathKey = CreatePathKey(normalizedPath);
        var pathPrefix = CreatePathKey(AppendDirectorySeparator(normalizedPath));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = (SqliteTransaction)transaction;
            deleteCommand.CommandText =
                """
                DELETE FROM asset_directory_exclusions
                WHERE path_key = $path_key;
                """;
            deleteCommand.Parameters.AddWithValue("$path_key", pathKey);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var locationCommand = connection.CreateCommand())
        {
            locationCommand.Transaction = (SqliteTransaction)transaction;
            locationCommand.CommandText =
                """
                UPDATE asset_locations
                SET excluded_from_asset_list = CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM asset_directory_exclusions exclusion
                            WHERE asset_locations.path_key = exclusion.path_key
                               OR substr(
                                    asset_locations.path_key,
                                    1,
                                    length(exclusion.path_prefix)) = exclusion.path_prefix
                        ) THEN 1
                        ELSE 0
                    END,
                    excluded_from_asset_list_at = CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM asset_directory_exclusions exclusion
                            WHERE asset_locations.path_key = exclusion.path_key
                               OR substr(
                                    asset_locations.path_key,
                                    1,
                                    length(exclusion.path_prefix)) = exclusion.path_prefix
                        ) THEN excluded_from_asset_list_at
                        ELSE NULL
                    END
                WHERE location_type = 'Local'
                  AND (
                      path_key = $path_key
                      OR substr(path_key, 1, length($path_prefix)) = $path_prefix
                  );
                """;
            locationCommand.Parameters.AddWithValue("$path_key", pathKey);
            locationCommand.Parameters.AddWithValue("$path_prefix", pathPrefix);
            await locationCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string AppendDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ||
            path.EndsWith(Path.AltDirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
    }
}
