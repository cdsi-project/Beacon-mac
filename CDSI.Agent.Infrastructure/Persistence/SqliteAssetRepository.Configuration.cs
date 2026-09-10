using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Workspaces;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository
{
    public async Task<IReadOnlyList<ScanRoot>> ListScanRootsAsync(
        bool includeRemoved = false,
        CancellationToken cancellationToken = default)
    {
        var roots = new List<ScanRoot>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, path, mode, enabled, status, created_at,
                updated_at, last_scanned_at, removed_at,
                volume_id, volume_relative_path, file_type_filter,
                extension_whitelist_json, file_type_filters_json,
                idle_scan_enabled, idle_scan_interval, idle_scan_unit
            FROM scan_roots
            WHERE $include_removed = 1 OR removed_at IS NULL
            ORDER BY mode DESC, path;
            """;
        command.Parameters.AddWithValue("$include_removed", includeRemoved ? 1 : 0);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roots.Add(ReadScanRoot(reader));
        }

        return roots;
    }

    public async Task SetScanRootEnabledAsync(
        Guid scanRootId,
        bool enabled,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_roots
            SET enabled = $enabled,
                status = CASE
                    WHEN $enabled = 0 THEN 'Disabled'
                    WHEN volume_id IS NOT NULL AND EXISTS (
                        SELECT 1
                        FROM local_volumes v
                        WHERE v.id = scan_roots.volume_id
                          AND v.is_online = 0
                    ) THEN 'Offline'
                    ELSE 'Active'
                END,
                updated_at = $updated_at
            WHERE id = $id AND removed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", scanRootId.ToString("D"));
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetScanRootFileFilterAsync(
        Guid scanRootId,
        ScanFileFilter fileFilter,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileFilter);
        var extensionWhitelistJson = JsonSerializer.Serialize(
            fileFilter.ExtensionWhitelist);
        var fileTypeFiltersJson = JsonSerializer.Serialize(
            fileFilter.FileTypeFilters.Select(fileType => fileType.ToString()));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_roots
            SET file_type_filter = $file_type_filter,
                extension_whitelist_json = $extension_whitelist_json,
                file_type_filters_json = $file_type_filters_json,
                last_scanned_at = CASE
                    WHEN file_type_filter <> $file_type_filter
                      OR extension_whitelist_json <> $extension_whitelist_json
                      OR file_type_filters_json <> $file_type_filters_json
                    THEN NULL
                    ELSE last_scanned_at
                END,
                updated_at = $updated_at
            WHERE id = $id AND removed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", scanRootId.ToString("D"));
        command.Parameters.AddWithValue(
            "$file_type_filter",
            fileFilter.FileTypeFilter.ToString());
        command.Parameters.AddWithValue(
            "$extension_whitelist_json",
            extensionWhitelistJson);
        command.Parameters.AddWithValue(
            "$file_type_filters_json",
            fileTypeFiltersJson);
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetScanRootIdleScheduleAsync(
        Guid scanRootId,
        IdleScanSchedule schedule,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_roots
            SET idle_scan_enabled = $enabled,
                idle_scan_interval = $interval,
                idle_scan_unit = $unit,
                updated_at = $updated_at
            WHERE id = $id AND removed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", scanRootId.ToString("D"));
        command.Parameters.AddWithValue("$enabled", schedule.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$interval", schedule.Interval);
        command.Parameters.AddWithValue("$unit", schedule.Unit.ToString());
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveScanRootAsync(
        Guid scanRootId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_roots
            SET enabled = 0,
                status = 'Removed',
                updated_at = $updated_at,
                removed_at = $removed_at
            WHERE id = $id AND removed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", scanRootId.ToString("D"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        command.Parameters.AddWithValue("$removed_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetScanRootStatusAsync(
        Guid scanRootId,
        ScanRootStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (status is ScanRootStatus.Disabled or ScanRootStatus.Removed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                "Use the enable or remove operation for this status.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_roots
            SET status = $status,
                updated_at = $updated_at
            WHERE id = $id AND removed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", scanRootId.ToString("D"));
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ManagedWorkspace?> GetManagedWorkspaceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, device_id, path, created_at, updated_at
            FROM managed_workspaces
            WHERE device_id = $device_id;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadManagedWorkspace(reader)
            : null;
    }

    public async Task<ManagedWorkspace> SaveManagedWorkspaceAsync(
        string deviceId,
        string path,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        var pathKey = CreatePathKey(normalizedPath);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO managed_workspaces(
                    id, device_id, path, path_key, created_at, updated_at)
                VALUES (
                    $id, $device_id, $path, $path_key, $created_at, $updated_at)
                ON CONFLICT(device_id) DO UPDATE SET
                    path = excluded.path,
                    path_key = excluded.path_key,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$device_id", deviceId);
            command.Parameters.AddWithValue("$path", normalizedPath);
            command.Parameters.AddWithValue("$path_key", pathKey);
            command.Parameters.AddWithValue("$created_at", now.ToString("O"));
            command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText =
            """
            SELECT id, device_id, path, created_at, updated_at
            FROM managed_workspaces
            WHERE device_id = $device_id;
            """;
        selectCommand.Parameters.AddWithValue("$device_id", deviceId);

        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Unable to load the managed workspace.");
        }

        return ReadManagedWorkspace(reader);
    }

    private static ScanRoot ReadScanRoot(SqliteDataReader reader)
    {
        var fileTypeFilter = Enum.Parse<AssetFileTypeFilter>(reader.GetString(11));
        var extensionWhitelist = JsonSerializer.Deserialize<string[]>(reader.GetString(12))
            ?? Array.Empty<string>();
        var fileTypeFilters = (JsonSerializer.Deserialize<string[]>(reader.GetString(13))
                ?? Array.Empty<string>())
            .Select(value => Enum.Parse<AssetFileTypeFilter>(value))
            .ToArray();
        var fileFilter = new ScanFileFilter(fileTypeFilters, extensionWhitelist);

        return new ScanRoot(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Enum.Parse<ScanRootMode>(reader.GetString(2)),
            reader.GetInt64(3) != 0,
            Enum.Parse<ScanRootStatus>(reader.GetString(4)),
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)),
            reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
            reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            fileFilter.FileTypeFilter,
            fileFilter.ExtensionWhitelist,
            fileFilter.FileTypeFilters,
            new IdleScanSchedule(
                reader.GetInt64(14) != 0,
                reader.GetInt32(15),
                Enum.Parse<IdleScanIntervalUnit>(reader.GetString(16))));
    }

    private static ManagedWorkspace ReadManagedWorkspace(SqliteDataReader reader)
    {
        return new ManagedWorkspace(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            ParseTimestamp(reader.GetString(4)));
    }
}
