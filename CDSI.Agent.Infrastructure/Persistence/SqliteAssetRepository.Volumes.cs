using CDSI.Agent.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository
{
    public async Task<LocalVolumeReconciliationResult> ReconcileLocalVolumesAsync(
        IReadOnlyCollection<LocalVolumeDescriptor> mountedVolumes,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mountedVolumes);
        var normalizedVolumes = NormalizeVolumes(mountedVolumes);
        var mountedIds = normalizedVolumes
            .Select(volume => volume.StableId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken);
        var trackedVolumes = await LoadTrackedVolumesAsync(
            connection,
            transaction,
            cancellationToken);
        var newlyTrackedVolumes = 0;
        var boundScanRoots = 0;
        var boundAssetLocations = 0;
        var remappedScanRoots = 0;
        var remappedAssetLocations = 0;
        var reconnectedVolumes = 0;

        foreach (var volume in normalizedVolumes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!trackedVolumes.TryGetValue(volume.StableId, out var tracked))
            {
                if (!await HasUnboundPathOnMountAsync(
                        connection,
                        transaction,
                        volume.MountPath,
                        cancellationToken))
                {
                    continue;
                }

                tracked = new TrackedLocalVolume(
                    Guid.NewGuid(),
                    volume.StableId,
                    volume.SerialNumber,
                    volume.MountPath,
                    IsOnline: true);
                await InsertTrackedVolumeAsync(
                    connection,
                    transaction,
                    tracked.Id,
                    volume,
                    now,
                    cancellationToken);
                trackedVolumes.Add(volume.StableId, tracked);
                newlyTrackedVolumes++;
            }
            else
            {
                if (!string.Equals(
                        tracked.SerialNumber,
                        volume.SerialNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"卷身份冲突，GUID 对应的序列号已变化: {volume.StableId}");
                }

                if (!PathsEqual(tracked.MountPath, volume.MountPath))
                {
                    var remapped = await RemapBoundPathsAsync(
                        connection,
                        transaction,
                        tracked.Id,
                        tracked.MountPath,
                        volume.MountPath,
                        now,
                        cancellationToken);
                    remappedScanRoots += remapped.ScanRoots;
                    remappedAssetLocations += remapped.AssetLocations;
                }

                await UpdateTrackedVolumeAsync(
                    connection,
                    transaction,
                    tracked.Id,
                    volume,
                    now,
                    cancellationToken);
                if (!tracked.IsOnline)
                {
                    await RestoreMountedVolumeStatusesAsync(
                        connection,
                        transaction,
                        tracked.Id,
                        now,
                        cancellationToken);
                    reconnectedVolumes++;
                }
            }

            var bound = await BindUnassignedPathsAsync(
                connection,
                transaction,
                tracked.Id,
                volume.MountPath,
                cancellationToken);
            boundScanRoots += bound.ScanRoots;
            boundAssetLocations += bound.AssetLocations;
        }

        var offlineVolumes = 0;
        foreach (var tracked in trackedVolumes.Values)
        {
            if (!tracked.IsOnline || mountedIds.Contains(tracked.StableId))
            {
                continue;
            }

            await MarkTrackedVolumeOfflineAsync(
                connection,
                transaction,
                tracked.Id,
                now,
                cancellationToken);
            offlineVolumes++;
        }

        await transaction.CommitAsync(cancellationToken);
        return new LocalVolumeReconciliationResult(
            normalizedVolumes.Count,
            newlyTrackedVolumes,
            boundScanRoots,
            boundAssetLocations,
            remappedScanRoots,
            remappedAssetLocations,
            reconnectedVolumes,
            offlineVolumes);
    }

    private static IReadOnlyList<LocalVolumeDescriptor> NormalizeVolumes(
        IReadOnlyCollection<LocalVolumeDescriptor> mountedVolumes)
    {
        var normalized = new Dictionary<string, LocalVolumeDescriptor>(
            StringComparer.OrdinalIgnoreCase);
        var mountPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in mountedVolumes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(volume.StableId);
            ArgumentException.ThrowIfNullOrWhiteSpace(volume.SerialNumber);
            ArgumentException.ThrowIfNullOrWhiteSpace(volume.MountPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(volume.DriveType);

            var mountPath = NormalizePath(volume.MountPath);
            if (!string.Equals(
                    mountPath,
                    Path.GetPathRoot(mountPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"卷挂载点必须是根路径: {volume.MountPath}",
                    nameof(mountedVolumes));
            }

            var descriptor = volume with
            {
                StableId = volume.StableId.Trim().ToUpperInvariant(),
                SerialNumber = volume.SerialNumber.Trim().ToUpperInvariant(),
                MountPath = mountPath,
                Label = NullIfWhiteSpace(volume.Label),
                FileSystem = NullIfWhiteSpace(volume.FileSystem),
                DriveType = volume.DriveType.Trim()
            };
            if (!normalized.TryAdd(descriptor.StableId, descriptor))
            {
                throw new ArgumentException(
                    $"检测到重复卷身份: {descriptor.StableId}",
                    nameof(mountedVolumes));
            }

            if (!mountPaths.Add(CreatePathKey(descriptor.MountPath)))
            {
                throw new ArgumentException(
                    $"检测到重复卷挂载点: {descriptor.MountPath}",
                    nameof(mountedVolumes));
            }
        }

        return normalized.Values.ToArray();
    }

    private static async Task<Dictionary<string, TrackedLocalVolume>>
        LoadTrackedVolumesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        var volumes = new Dictionary<string, TrackedLocalVolume>(
            StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, stable_id, serial_number, mount_path, is_online
            FROM local_volumes;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var volume = new TrackedLocalVolume(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4) != 0);
            volumes.Add(volume.StableId, volume);
        }

        return volumes;
    }

    private static async Task<bool> HasUnboundPathOnMountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string mountPath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT path
            FROM scan_roots
            WHERE volume_id IS NULL
            UNION ALL
            SELECT path
            FROM managed_workspaces
            WHERE volume_id IS NULL
            UNION ALL
            SELECT path
            FROM asset_locations
            WHERE volume_id IS NULL AND location_type = 'Local';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (TryGetVolumeRelativePath(mountPath, reader.GetString(0), out _))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task InsertTrackedVolumeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid volumeId,
        LocalVolumeDescriptor volume,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO local_volumes(
                id, stable_id, serial_number, label, filesystem, drive_type,
                mount_path, mount_path_key, is_online,
                first_seen_at, last_seen_at, updated_at)
            VALUES (
                $id, $stable_id, $serial_number, $label, $filesystem, $drive_type,
                $mount_path, $mount_path_key, 1,
                $first_seen_at, $last_seen_at, $updated_at);
            """;
        AddVolumeParameters(command, volumeId, volume, now);
        command.Parameters.AddWithValue("$first_seen_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateTrackedVolumeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid volumeId,
        LocalVolumeDescriptor volume,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE local_volumes
            SET serial_number = $serial_number,
                label = $label,
                filesystem = $filesystem,
                drive_type = $drive_type,
                mount_path = $mount_path,
                mount_path_key = $mount_path_key,
                is_online = 1,
                last_seen_at = $last_seen_at,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        AddVolumeParameters(command, volumeId, volume, now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddVolumeParameters(
        SqliteCommand command,
        Guid volumeId,
        LocalVolumeDescriptor volume,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", volumeId.ToString("D"));
        command.Parameters.AddWithValue("$stable_id", volume.StableId);
        command.Parameters.AddWithValue("$serial_number", volume.SerialNumber);
        command.Parameters.AddWithValue("$label", (object?)volume.Label ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$filesystem",
            (object?)volume.FileSystem ?? DBNull.Value);
        command.Parameters.AddWithValue("$drive_type", volume.DriveType);
        command.Parameters.AddWithValue("$mount_path", volume.MountPath);
        command.Parameters.AddWithValue(
            "$mount_path_key",
            CreatePathKey(volume.MountPath));
        command.Parameters.AddWithValue("$last_seen_at", now.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
    }

    private static async Task<PathUpdateCounts> RemapBoundPathsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid volumeId,
        string oldMountPath,
        string newMountPath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var scanRoots = await LoadBoundPathsAsync(
            connection,
            transaction,
            "scan_roots",
            volumeId,
            cancellationToken);
        foreach (var item in scanRoots)
        {
            var relativePath = item.RelativePath ??
                GetRequiredVolumeRelativePath(oldMountPath, item.Path);
            await UpdateBoundPathAsync(
                connection,
                transaction,
                "scan_roots",
                item.Id,
                CombineVolumePath(newMountPath, relativePath),
                relativePath,
                now,
                cancellationToken);
        }

        var workspaces = await LoadBoundPathsAsync(
            connection,
            transaction,
            "managed_workspaces",
            volumeId,
            cancellationToken);
        foreach (var item in workspaces)
        {
            var relativePath = item.RelativePath ??
                GetRequiredVolumeRelativePath(oldMountPath, item.Path);
            await UpdateBoundPathAsync(
                connection,
                transaction,
                "managed_workspaces",
                item.Id,
                CombineVolumePath(newMountPath, relativePath),
                relativePath,
                now,
                cancellationToken);
        }

        var locations = await LoadBoundPathsAsync(
            connection,
            transaction,
            "asset_locations",
            volumeId,
            cancellationToken);
        foreach (var item in locations)
        {
            var relativePath = item.RelativePath ??
                GetRequiredVolumeRelativePath(oldMountPath, item.Path);
            await UpdateBoundPathAsync(
                connection,
                transaction,
                "asset_locations",
                item.Id,
                CombineVolumePath(newMountPath, relativePath),
                relativePath,
                now,
                cancellationToken);
        }

        return new PathUpdateCounts(scanRoots.Count, locations.Count);
    }

    private static async Task<IReadOnlyList<BoundPath>> LoadBoundPathsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        Guid volumeId,
        CancellationToken cancellationToken)
    {
        var paths = new List<BoundPath>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT id, path, volume_relative_path
            FROM {tableName}
            WHERE volume_id = $volume_id;
            """;
        command.Parameters.AddWithValue("$volume_id", volumeId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(new BoundPath(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return paths;
    }

    private static async Task UpdateBoundPathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string id,
        string path,
        string relativePath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var updatedAtAssignment = tableName is "scan_roots" or "managed_workspaces"
            ? ", updated_at = $updated_at"
            : string.Empty;
        command.CommandText =
            $"""
            UPDATE {tableName}
            SET path = $path,
                path_key = $path_key,
                volume_relative_path = $relative_path
                {updatedAtAssignment}
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$path_key", CreatePathKey(path));
        command.Parameters.AddWithValue("$relative_path", relativePath);
        if (updatedAtAssignment.Length > 0)
        {
            command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PathUpdateCounts> BindUnassignedPathsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid volumeId,
        string mountPath,
        CancellationToken cancellationToken)
    {
        var scanRoots = await BindUnassignedTablePathsAsync(
            connection,
            transaction,
            "scan_roots",
            volumeId,
            mountPath,
            localLocationsOnly: false,
            cancellationToken);
        await BindUnassignedTablePathsAsync(
            connection,
            transaction,
            "managed_workspaces",
            volumeId,
            mountPath,
            localLocationsOnly: false,
            cancellationToken);
        var assetLocations = await BindUnassignedTablePathsAsync(
            connection,
            transaction,
            "asset_locations",
            volumeId,
            mountPath,
            localLocationsOnly: true,
            cancellationToken);
        return new PathUpdateCounts(scanRoots, assetLocations);
    }

    private static async Task<int> BindUnassignedTablePathsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        Guid volumeId,
        string mountPath,
        bool localLocationsOnly,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string Id, string Path)>();
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.Transaction = transaction;
            selectCommand.CommandText =
                $"""
                SELECT id, path
                FROM {tableName}
                WHERE volume_id IS NULL
                  {(localLocationsOnly ? "AND location_type = 'Local'" : string.Empty)};
                """;
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var bound = 0;
        foreach (var candidate in candidates)
        {
            if (!TryGetVolumeRelativePath(mountPath, candidate.Path, out var relativePath))
            {
                continue;
            }

            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                $"""
                UPDATE {tableName}
                SET volume_id = $volume_id,
                    volume_relative_path = $relative_path
                WHERE id = $id AND volume_id IS NULL;
                """;
            updateCommand.Parameters.AddWithValue("$id", candidate.Id);
            updateCommand.Parameters.AddWithValue("$volume_id", volumeId.ToString("D"));
            updateCommand.Parameters.AddWithValue("$relative_path", relativePath);
            bound += await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return bound;
    }

    private static async Task RestoreMountedVolumeStatusesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid volumeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE scan_roots
            SET status = CASE WHEN enabled = 1 THEN 'Active' ELSE 'Disabled' END,
                updated_at = $updated_at
            WHERE volume_id = $volume_id
              AND removed_at IS NULL
              AND status = 'Offline';

            UPDATE asset_locations
            SET status = 'Unverified'
            WHERE volume_id = $volume_id
              AND location_type = 'Local'
              AND status = 'Offline';
            """;
        command.Parameters.AddWithValue("$volume_id", volumeId.ToString("D"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<VolumeMountBinding>>
        LoadOnlineVolumeMountsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        var mounts = new List<VolumeMountBinding>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, mount_path
            FROM local_volumes
            WHERE is_online = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mounts.Add(new VolumeMountBinding(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1)));
        }

        return mounts
            .OrderByDescending(mount => mount.MountPath.Length)
            .ToArray();
    }

    private static LocalPathVolumeBinding? FindVolumeBinding(
        IReadOnlyList<VolumeMountBinding> mounts,
        string path)
    {
        foreach (var mount in mounts)
        {
            if (TryGetVolumeRelativePath(
                    mount.MountPath,
                    path,
                    out var relativePath))
            {
                return new LocalPathVolumeBinding(mount.VolumeId, relativePath);
            }
        }

        return null;
    }

    private static void AddVolumeBindingParameters(
        SqliteCommand command,
        LocalPathVolumeBinding? binding)
    {
        command.Parameters.AddWithValue(
            "$volume_id",
            binding is null ? DBNull.Value : binding.VolumeId.ToString("D"));
        command.Parameters.AddWithValue(
            "$volume_relative_path",
            binding is null ? DBNull.Value : binding.RelativePath);
    }

    private static async Task MarkTrackedVolumeOfflineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid volumeId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE local_volumes
            SET is_online = 0,
                updated_at = $updated_at
            WHERE id = $volume_id;

            UPDATE scan_roots
            SET status = 'Offline',
                updated_at = $updated_at
            WHERE volume_id = $volume_id
              AND enabled = 1
              AND removed_at IS NULL;

            UPDATE asset_locations
            SET status = 'Offline'
            WHERE volume_id = $volume_id
              AND location_type = 'Local'
              AND status IN ('Available', 'Unverified');
            """;
        command.Parameters.AddWithValue("$volume_id", volumeId.ToString("D"));
        command.Parameters.AddWithValue("$updated_at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool TryGetVolumeRelativePath(
        string mountPath,
        string candidatePath,
        out string relativePath)
    {
        var normalizedMount = NormalizePath(mountPath);
        var normalizedCandidate = NormalizePath(candidatePath);
        var relative = Path.GetRelativePath(normalizedMount, normalizedCandidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = relative == "." ? string.Empty : relative;
        return true;
    }

    private static string GetRequiredVolumeRelativePath(
        string mountPath,
        string path)
    {
        return TryGetVolumeRelativePath(mountPath, path, out var relativePath)
            ? relativePath
            : throw new InvalidOperationException(
                $"路径不属于已登记卷: {path}");
    }

    private static string CombineVolumePath(string mountPath, string relativePath)
    {
        return string.IsNullOrEmpty(relativePath)
            ? NormalizePath(mountPath)
            : NormalizePath(Path.Combine(mountPath, relativePath));
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record TrackedLocalVolume(
        Guid Id,
        string StableId,
        string SerialNumber,
        string MountPath,
        bool IsOnline);

    private sealed record BoundPath(
        string Id,
        string Path,
        string? RelativePath);

    private sealed record PathUpdateCounts(int ScanRoots, int AssetLocations);

    private sealed record VolumeMountBinding(Guid VolumeId, string MountPath);

    private sealed record LocalPathVolumeBinding(Guid VolumeId, string RelativePath);
}
