using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Application.Storage;

public sealed class ObjectStorageManagementService
{
    private readonly IObjectStorageUploadRepository _repository;
    private readonly ObjectStorageProfileService _profileService;
    private readonly IReadOnlyDictionary<ObjectStorageProvider, IObjectStorageAdapter>
        _adapters;

    public ObjectStorageManagementService(
        IObjectStorageUploadRepository repository,
        ObjectStorageProfileService profileService,
        IEnumerable<IObjectStorageAdapter> adapters)
    {
        _repository = repository;
        _profileService = profileService;
        _adapters = adapters.ToDictionary(adapter => adapter.Provider);
    }

    public async Task<IReadOnlyList<ManagedObjectStorageBackup>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = (await _profileService.ListAsync(cancellationToken))
            .ToDictionary(item => item.Profile.Id);
        var sources = await _repository.ListManagedObjectStorageBackupsAsync(
            cancellationToken);
        return sources.Select(source =>
        {
            profiles.TryGetValue(source.Location.StorageProfileId, out var configured);
            return new ManagedObjectStorageBackup(
                source,
                configured?.Profile,
                configured?.HasStoredSecret == true);
        }).ToArray();
    }

    public async Task DeleteAsync(
        Guid storageLocationId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(storageLocationId, cancellationToken);
        var source = resolved.Source;
        var location = source.Location;
        var remote = await resolved.Adapter.StatAsync(
            resolved.Connection,
            location.ObjectKey,
            cancellationToken);
        if (remote is not null)
        {
            EnsureMatches(location, remote, "待删除的云端对象");
            await resolved.Adapter.DeleteAsync(
                resolved.Connection,
                location.ObjectKey,
                cancellationToken);
            if (await resolved.Adapter.StatAsync(
                    resolved.Connection,
                    location.ObjectKey,
                    cancellationToken) is not null)
            {
                throw new IOException("云端存储仍能读取已删除对象，本地备份记录已保留。");
            }
        }

        if (!await _repository.DeleteObjectStorageLocationAsync(
                location.Id,
                location.ObjectKey,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "云端对象已删除，但本地备份记录已被其他操作修改，请刷新后重试。");
        }
    }

    public async Task<ObjectStorageRenameResult> RenameAsync(
        Guid storageLocationId,
        string newFilename,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(storageLocationId, cancellationToken);
        var source = resolved.Source;
        var location = source.Location;
        if (!ObjectStorageObjectKey.TryRenameFile(
                location.ObjectKey,
                newFilename,
                out var destinationKey,
                out var errorMessage))
        {
            throw new ArgumentException(errorMessage, nameof(newFilename));
        }

        if (string.Equals(
                location.ObjectKey,
                destinationKey,
                StringComparison.Ordinal))
        {
            return new ObjectStorageRenameResult(
                new ManagedObjectStorageBackup(
                    source,
                    resolved.Connection.Profile,
                    HasStoredSecret: true),
                OldObjectDeleted: true,
                WarningMessage: null);
        }

        var existingDestination = await resolved.Adapter.StatAsync(
            resolved.Connection,
            destinationKey,
            cancellationToken);
        if (existingDestination is not null)
        {
            throw new IOException("同一云端目录中已存在该文件名，未执行重命名。");
        }

        var oldRemote = await resolved.Adapter.StatAsync(
            resolved.Connection,
            location.ObjectKey,
            cancellationToken)
            ?? throw new FileNotFoundException(
                "待重命名的云端对象不存在，本地备份记录已保留。",
                location.ObjectKey);
        EnsureMatches(location, oldRemote, "待重命名的云端对象");

        ObjectStorageObjectInfo copied;
        try
        {
            copied = await resolved.Adapter.CopyAsync(
                new ObjectStorageCopyRequest(
                    resolved.Connection,
                    source.AssetId,
                    location.ObjectKey,
                    destinationKey,
                    location.Size,
                    location.Sha256,
                    oldRemote.ETag),
                cancellationToken);
            EnsureMatches(location, copied, "重命名后的云端对象");
        }
        catch
        {
            try
            {
                if (await resolved.Adapter.StatAsync(
                        resolved.Connection,
                        destinationKey,
                        CancellationToken.None) is not null)
                {
                    await resolved.Adapter.DeleteAsync(
                        resolved.Connection,
                        destinationKey,
                        CancellationToken.None);
                }
            }
            catch
            {
            }

            throw;
        }

        var now = DateTimeOffset.UtcNow;
        var updatedLocation = location with
        {
            ObjectKey = destinationKey,
            Status = StorageVerificationStatus.Healthy,
            ETag = copied.ETag,
            UpdatedAt = now,
            LastVerifiedAt = now
        };
        try
        {
            if (!await _repository.ReplaceObjectStorageLocationAsync(
                    updatedLocation,
                    location.ObjectKey,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "本地备份记录已被其他操作修改，未完成重命名。");
            }
        }
        catch (Exception updateException)
        {
            try
            {
                await resolved.Adapter.DeleteAsync(
                    resolved.Connection,
                    destinationKey,
                    CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                throw new InvalidOperationException(
                    "本地备份记录更新失败，且未能清理新复制的云端对象。",
                    new AggregateException(updateException, cleanupException));
            }

            throw;
        }

        string? warning = null;
        var oldObjectDeleted = true;
        try
        {
            await resolved.Adapter.DeleteAsync(
                resolved.Connection,
                location.ObjectKey,
                cancellationToken);
            oldObjectDeleted = await resolved.Adapter.StatAsync(
                resolved.Connection,
                location.ObjectKey,
                cancellationToken) is null;
            if (!oldObjectDeleted)
            {
                warning = "新文件已生效，但旧云端对象仍然存在，请稍后手动清理。";
            }
        }
        catch (Exception exception)
        {
            oldObjectDeleted = false;
            warning = $"新文件已生效，但旧云端对象删除失败：{exception.Message}";
        }

        var updatedSource = source with { Location = updatedLocation };
        return new ObjectStorageRenameResult(
            new ManagedObjectStorageBackup(
                updatedSource,
                resolved.Connection.Profile,
                HasStoredSecret: true),
            oldObjectDeleted,
            warning);
    }

    private async Task<ResolvedBackup> ResolveAsync(
        Guid storageLocationId,
        CancellationToken cancellationToken)
    {
        var source = await _repository.GetManagedObjectStorageBackupAsync(
            storageLocationId,
            cancellationToken)
            ?? throw new InvalidOperationException("云备份记录不存在或已被删除。");
        var connection = await _profileService.GetConnectionAsync(
            source.Location.StorageProfileId,
            cancellationToken);
        if (!_adapters.TryGetValue(connection.Profile.Provider, out var adapter))
        {
            throw new NotSupportedException(
                $"尚未安装 {connection.Profile.Provider} 存储适配器。");
        }

        return new ResolvedBackup(source, connection, adapter);
    }

    private static void EnsureMatches(
        ObjectStorageLocation expected,
        ObjectStorageObjectInfo actual,
        string displayName)
    {
        if (string.IsNullOrWhiteSpace(expected.Sha256))
        {
            throw new InvalidOperationException(
                $"{displayName}缺少本地 SHA-256 记录，无法安全执行操作。");
        }

        if (actual.Size != expected.Size)
        {
            throw new IOException($"{displayName}大小与本地记录不一致，操作已停止。");
        }

        if (!string.Equals(
                expected.Sha256,
                actual.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"{displayName}校验值与本地记录不一致，操作已停止。");
        }
    }

    private sealed record ResolvedBackup(
        ObjectStorageRestoreSource Source,
        ObjectStorageConnection Connection,
        IObjectStorageAdapter Adapter);
}

public sealed record ManagedObjectStorageBackup(
    ObjectStorageRestoreSource Source,
    ObjectStorageProfile? Profile,
    bool HasStoredSecret)
{
    public bool IsAvailable => Profile is not null && HasStoredSecret;
}

public sealed record ObjectStorageRenameResult(
    ManagedObjectStorageBackup Backup,
    bool OldObjectDeleted,
    string? WarningMessage);
