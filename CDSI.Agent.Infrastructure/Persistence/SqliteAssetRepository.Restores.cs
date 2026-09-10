using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository : IObjectStorageRestoreRepository
{
    public async Task CreateRestoreJobAsync(
        ObjectStorageRestoreJob job,
        IReadOnlyCollection<ObjectStorageRestoreItem> items,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO restore_jobs(
                    id, status, destination_kind, target_directory,
                    started_at, finished_at, total_items, completed_items,
                    failed_items, total_bytes, downloaded_bytes, error_message)
                VALUES(
                    $id, $status, $destination_kind, $target_directory,
                    $started_at, $finished_at, $total_items, $completed_items,
                    $failed_items, $total_bytes, $downloaded_bytes, $error_message);
                """;
            AddRestoreJobParameters(command, job);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO restore_items(
                    id, job_id, asset_id, storage_profile_id, object_key,
                    target_path, status, size, downloaded_bytes, sha256,
                    error_message, finished_at)
                VALUES(
                    $id, $job_id, $asset_id, $storage_profile_id, $object_key,
                    $target_path, $status, $size, $downloaded_bytes, $sha256,
                    $error_message, $finished_at);
                """;
            AddRestoreItemParameters(command, item);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveRestoreItemAsync(
        ObjectStorageRestoreItem item,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE restore_items
            SET target_path = $target_path,
                status = $status,
                downloaded_bytes = $downloaded_bytes,
                sha256 = $sha256,
                error_message = $error_message,
                finished_at = $finished_at
            WHERE id = $id AND job_id = $job_id;
            """;
        AddRestoreItemParameters(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateRestoreJobAsync(
        ObjectStorageRestoreJob job,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE restore_jobs
            SET status = $status,
                finished_at = $finished_at,
                completed_items = $completed_items,
                failed_items = $failed_items,
                total_bytes = $total_bytes,
                downloaded_bytes = $downloaded_bytes,
                error_message = $error_message
            WHERE id = $id;
            """;
        AddRestoreJobParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ObjectStorageRestoreAudit?> GetRestoreJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        ObjectStorageRestoreJob? job;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, status, destination_kind, target_directory,
                       started_at, finished_at, total_items, completed_items,
                       failed_items, total_bytes, downloaded_bytes, error_message
                FROM restore_jobs WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", jobId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            job = await reader.ReadAsync(cancellationToken)
                ? ReadRestoreJob(reader)
                : null;
        }

        if (job is null)
        {
            return null;
        }

        var items = new List<ObjectStorageRestoreItem>();
        await using var itemCommand = connection.CreateCommand();
        itemCommand.CommandText =
            """
            SELECT id, job_id, asset_id, storage_profile_id, object_key,
                   target_path, status, size, downloaded_bytes, sha256,
                   error_message, finished_at
            FROM restore_items
            WHERE job_id = $job_id
            ORDER BY rowid;
            """;
        itemCommand.Parameters.AddWithValue("$job_id", jobId.ToString("D"));
        await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            items.Add(ReadRestoreItem(itemReader));
        }

        return new ObjectStorageRestoreAudit(job, items);
    }

    private static void AddRestoreJobParameters(
        SqliteCommand command,
        ObjectStorageRestoreJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue(
            "$destination_kind",
            job.DestinationKind.ToString());
        command.Parameters.AddWithValue("$target_directory", job.TargetDirectory);
        command.Parameters.AddWithValue("$started_at", job.StartedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$finished_at",
            job.FinishedAt is null ? DBNull.Value : job.FinishedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$total_items", job.TotalItems);
        command.Parameters.AddWithValue("$completed_items", job.CompletedItems);
        command.Parameters.AddWithValue("$failed_items", job.FailedItems);
        command.Parameters.AddWithValue("$total_bytes", job.TotalBytes);
        command.Parameters.AddWithValue("$downloaded_bytes", job.DownloadedBytes);
        command.Parameters.AddWithValue(
            "$error_message",
            (object?)job.ErrorMessage ?? DBNull.Value);
    }

    private static void AddRestoreItemParameters(
        SqliteCommand command,
        ObjectStorageRestoreItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("$job_id", item.JobId.ToString("D"));
        command.Parameters.AddWithValue("$asset_id", item.AssetId.ToString("D"));
        command.Parameters.AddWithValue(
            "$storage_profile_id",
            item.StorageProfileId.ToString("D"));
        command.Parameters.AddWithValue("$object_key", item.ObjectKey);
        command.Parameters.AddWithValue("$target_path", item.TargetPath);
        command.Parameters.AddWithValue("$status", item.Status.ToString());
        command.Parameters.AddWithValue("$size", item.Size);
        command.Parameters.AddWithValue("$downloaded_bytes", item.DownloadedBytes);
        command.Parameters.AddWithValue("$sha256", (object?)item.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$error_message",
            (object?)item.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$finished_at",
            item.FinishedAt is null ? DBNull.Value : item.FinishedAt.Value.ToString("O"));
    }

    private static ObjectStorageRestoreJob ReadRestoreJob(SqliteDataReader reader)
    {
        return new ObjectStorageRestoreJob(
            Guid.Parse(reader.GetString(0)),
            Enum.Parse<RestoreJobStatus>(reader.GetString(1)),
            Enum.Parse<ObjectStorageRestoreDestinationKind>(reader.GetString(2)),
            reader.GetString(3),
            ParseTimestamp(reader.GetString(4)),
            reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    private static ObjectStorageRestoreItem ReadRestoreItem(SqliteDataReader reader)
    {
        return new ObjectStorageRestoreItem(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            Enum.Parse<RestoreItemStatus>(reader.GetString(6)),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)));
    }
}
