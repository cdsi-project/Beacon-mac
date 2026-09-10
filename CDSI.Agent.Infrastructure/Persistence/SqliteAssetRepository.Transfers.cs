using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Transfers;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository
{
    public async Task<LocalAssetTransferSource?> GetLocalAssetTransferSourceAsync(
        Guid assetId,
        string deviceId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(sourcePath);
        var pathKey = CreatePathKey(normalizedPath);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                a.id,
                a.original_filename,
                a.extension,
                a.size,
                a.modified_at,
                a.sha256,
                l.path
            FROM assets a
            INNER JOIN asset_locations l ON l.asset_id = a.id
            WHERE a.id = $asset_id
              AND l.location_type = 'Local'
              AND l.device_id = $device_id
              AND l.path_key = $path_key
              AND l.status = 'Available';
            """;
        command.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$path_key", pathKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LocalAssetTransferSource(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            ParseTimestamp(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6));
    }

    public async Task RegisterManagedLocalLocationAsync(
        Guid assetId,
        string deviceId,
        string path,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        await RegisterLocalLocationAsync(
            assetId,
            deviceId,
            path,
            AssetLocationOwnership.Managed,
            verifiedAt,
            cancellationToken);
    }

    public async Task RegisterLocalLocationAsync(
        Guid assetId,
        string deviceId,
        string path,
        AssetLocationOwnership ownership,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        if (ownership is not (AssetLocationOwnership.External or
            AssetLocationOwnership.Managed))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }

        var normalizedPath = NormalizePath(path);
        var pathKey = CreatePathKey(normalizedPath);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var volumeBinding = FindVolumeBinding(
            await LoadOnlineVolumeMountsAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken),
            normalizedPath);

        await using (var conflictCommand = connection.CreateCommand())
        {
            conflictCommand.Transaction = (SqliteTransaction)transaction;
            conflictCommand.CommandText =
                """
                SELECT asset_id
                FROM asset_locations
                WHERE device_id = $device_id AND path_key = $path_key;
                """;
            conflictCommand.Parameters.AddWithValue("$device_id", deviceId);
            conflictCommand.Parameters.AddWithValue("$path_key", pathKey);
            var existingAssetId =
                await conflictCommand.ExecuteScalarAsync(cancellationToken) as string;
            if (existingAssetId is not null &&
                !string.Equals(
                    existingAssetId,
                    assetId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "目标路径已登记为另一个资产，未修改现有文件。");
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO asset_locations(
                id, asset_id, location_type, ownership, device_id, path, path_key,
                status, last_seen_at, last_verified_at,
                volume_id, volume_relative_path)
            VALUES (
                $id, $asset_id, 'Local', $ownership, $device_id, $path, $path_key,
                'Available', $verified_at, $verified_at,
                $volume_id, $volume_relative_path)
            ON CONFLICT(device_id, path_key) DO UPDATE SET
                path = excluded.path,
                ownership = excluded.ownership,
                status = 'Available',
                last_seen_at = excluded.last_seen_at,
                last_verified_at = excluded.last_verified_at,
                volume_id = CASE
                    WHEN excluded.volume_id IS NULL THEN asset_locations.volume_id
                    ELSE excluded.volume_id
                END,
                volume_relative_path = CASE
                    WHEN excluded.volume_id IS NULL THEN asset_locations.volume_relative_path
                    ELSE excluded.volume_relative_path
                END;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$ownership", ownership.ToString());
        command.Parameters.AddWithValue("$path", normalizedPath);
        command.Parameters.AddWithValue("$path_key", pathKey);
        AddVolumeBindingParameters(command, volumeBinding);
        command.Parameters.AddWithValue("$verified_at", verifiedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkLocalLocationMissingAsync(
        string deviceId,
        string path,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE asset_locations
            SET status = 'Missing',
                last_verified_at = $verified_at
            WHERE device_id = $device_id
              AND path_key = $path_key
              AND location_type = 'Local';
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$path_key", CreatePathKey(normalizedPath));
        command.Parameters.AddWithValue("$verified_at", verifiedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateFileOperationAsync(
        FileOperationRecord operation,
        IReadOnlyCollection<FileOperationItemRecord> items,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertFileOperationAsync(
            connection,
            (SqliteTransaction)transaction,
            operation,
            cancellationToken);

        foreach (var item in items)
        {
            await InsertFileOperationItemAsync(
                connection,
                (SqliteTransaction)transaction,
                item,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveFileOperationItemAsync(
        FileOperationItemRecord item,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE file_operation_items
            SET target_path = $target_path,
                status = $status,
                source_deleted = $source_deleted,
                sha256 = $sha256,
                error_message = $error_message,
                finished_at = $finished_at
            WHERE id = $id AND operation_id = $operation_id;
            """;
        AddFileOperationItemParameters(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateFileOperationAsync(
        FileOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE file_operations
            SET status = $status,
                finished_at = $finished_at,
                total_items = $total_items,
                completed_items = $completed_items,
                failed_items = $failed_items,
                error_message = $error_message
            WHERE id = $id;
            """;
        AddFileOperationParameters(command, operation);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FileOperationAudit?> GetFileOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        FileOperationRecord? operation;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    id, action, status, started_at, finished_at,
                    total_items, completed_items, failed_items, error_message
                FROM file_operations
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            operation = await reader.ReadAsync(cancellationToken)
                ? ReadFileOperation(reader)
                : null;
        }

        if (operation is null)
        {
            return null;
        }

        var items = new List<FileOperationItemRecord>();
        await using var itemCommand = connection.CreateCommand();
        itemCommand.CommandText =
            """
            SELECT
                id, operation_id, asset_id, source_path, target_path,
                status, source_deleted, sha256, error_message, finished_at
            FROM file_operation_items
            WHERE operation_id = $operation_id
            ORDER BY rowid;
            """;
        itemCommand.Parameters.AddWithValue("$operation_id", operationId.ToString("D"));
        await using var itemReader =
            await itemCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            items.Add(new FileOperationItemRecord(
                Guid.Parse(itemReader.GetString(0)),
                Guid.Parse(itemReader.GetString(1)),
                Guid.Parse(itemReader.GetString(2)),
                itemReader.GetString(3),
                itemReader.IsDBNull(4) ? null : itemReader.GetString(4),
                Enum.Parse<FileOperationItemStatus>(itemReader.GetString(5)),
                itemReader.GetInt32(6) != 0,
                itemReader.IsDBNull(7) ? null : itemReader.GetString(7),
                itemReader.IsDBNull(8) ? null : itemReader.GetString(8),
                itemReader.IsDBNull(9)
                    ? null
                    : ParseTimestamp(itemReader.GetString(9))));
        }

        return new FileOperationAudit(operation, items);
    }

    private static async Task InsertFileOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FileOperationRecord operation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO file_operations(
                id, action, status, started_at, finished_at,
                total_items, completed_items, failed_items, error_message)
            VALUES (
                $id, $action, $status, $started_at, $finished_at,
                $total_items, $completed_items, $failed_items, $error_message);
            """;
        AddFileOperationParameters(command, operation);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertFileOperationItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FileOperationItemRecord item,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO file_operation_items(
                id, operation_id, asset_id, source_path, target_path,
                status, source_deleted, sha256, error_message, finished_at)
            VALUES (
                $id, $operation_id, $asset_id, $source_path, $target_path,
                $status, $source_deleted, $sha256, $error_message, $finished_at);
            """;
        AddFileOperationItemParameters(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddFileOperationParameters(
        SqliteCommand command,
        FileOperationRecord operation)
    {
        command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        command.Parameters.AddWithValue("$action", operation.Action.ToString());
        command.Parameters.AddWithValue("$status", operation.Status.ToString());
        command.Parameters.AddWithValue("$started_at", operation.StartedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$finished_at",
            operation.FinishedAt is null
                ? DBNull.Value
                : operation.FinishedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$total_items", operation.TotalItems);
        command.Parameters.AddWithValue("$completed_items", operation.CompletedItems);
        command.Parameters.AddWithValue("$failed_items", operation.FailedItems);
        command.Parameters.AddWithValue(
            "$error_message",
            (object?)operation.ErrorMessage ?? DBNull.Value);
    }

    private static void AddFileOperationItemParameters(
        SqliteCommand command,
        FileOperationItemRecord item)
    {
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$operation_id", item.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$asset_id", item.AssetId.ToString("D"));
        command.Parameters.AddWithValue("$source_path", item.SourcePath);
        command.Parameters.AddWithValue(
            "$target_path",
            (object?)item.TargetPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", item.Status.ToString());
        command.Parameters.AddWithValue("$source_deleted", item.SourceDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$sha256", (object?)item.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$error_message",
            (object?)item.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$finished_at",
            item.FinishedAt is null
                ? DBNull.Value
                : item.FinishedAt.Value.ToString("O"));
    }

    private static FileOperationRecord ReadFileOperation(SqliteDataReader reader)
    {
        return new FileOperationRecord(
            Guid.Parse(reader.GetString(0)),
            Enum.Parse<ManagedAssetTransferAction>(reader.GetString(1)),
            Enum.Parse<FileOperationStatus>(reader.GetString(2)),
            ParseTimestamp(reader.GetString(3)),
            reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }
}
