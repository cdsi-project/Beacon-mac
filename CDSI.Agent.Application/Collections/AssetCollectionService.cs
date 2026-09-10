using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;

namespace CDSI.Agent.Application.Collections;

public sealed class AssetCollectionService(IAssetCollectionRepository repository)
{
    private const int MaximumNameLength = 120;

    public async Task<AssetCollection> CreateAsync(
        string name,
        AssetCollectionType type,
        IReadOnlyCollection<Guid>? backupProfileIds = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = ValidateDetails(name, type);

        var normalizedBackupProfileIds = backupProfileIds?
            .Distinct()
            .ToArray() ?? [];
        var now = DateTimeOffset.UtcNow;
        var collection = new AssetCollection(
            Guid.NewGuid(),
            normalizedName,
            type,
            now,
            now)
        {
            BackupProfileIds = normalizedBackupProfileIds
        };
        if (!await repository.CreateAssetCollectionAsync(collection, cancellationToken))
        {
            throw new InvalidOperationException("已存在同名项目。请使用其他名称。");
        }

        return collection;
    }

    public async Task<AssetCollection> UpdateAsync(
        Guid collectionId,
        string name,
        AssetCollectionType type,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = ValidateDetails(name, type);
        var existing = await GetRequiredAsync(collectionId, cancellationToken);
        var updated = existing with
        {
            Name = normalizedName,
            Type = type,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (!await repository.UpdateAssetCollectionAsync(updated, cancellationToken))
        {
            throw new InvalidOperationException("已存在同名项目。请使用其他名称。");
        }

        return updated;
    }

    public Task<IReadOnlyList<AssetCollectionSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return repository.ListAssetCollectionsAsync(cancellationToken);
    }

    public async Task<AssetCollection> DeleteAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetRequiredAsync(collectionId, cancellationToken);
        if (!await repository.DeleteAssetCollectionAsync(
                collectionId,
                DateTimeOffset.UtcNow,
                cancellationToken))
        {
            throw new KeyNotFoundException("资产清单不存在或已被移除。");
        }

        return collection;
    }

    public async Task<IReadOnlyList<AssetCollectionMember>> GetMembersAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        await GetRequiredAsync(collectionId, cancellationToken);
        return await repository.ListAssetCollectionMembersAsync(
            collectionId,
            cancellationToken);
    }

    public async Task<int> AddAssetsAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        await GetRequiredAsync(collectionId, cancellationToken);
        return await repository.AddAssetsToCollectionAsync(
            collectionId,
            assetIds.Distinct().ToArray(),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public async Task<int> RemoveAssetsAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        await GetRequiredAsync(collectionId, cancellationToken);
        return await repository.RemoveAssetsFromCollectionAsync(
            collectionId,
            assetIds.Distinct().ToArray(),
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public async Task<AssetCollectionSyncPlan> PrepareSyncAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var collection = await GetRequiredAsync(collectionId, cancellationToken);
        var members = await repository.ListAssetCollectionMembersAsync(
            collectionId,
            cancellationToken);
        return new AssetCollectionSyncPlan(collection, members);
    }

    public async Task<AssetCollectionSyncPlan> PrepareSelectedSyncAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var requestedIds = assetIds.Distinct().ToArray();
        if (requestedIds.Length == 0)
        {
            throw new ArgumentException("至少选择一个待同步资产。", nameof(assetIds));
        }

        var collection = await GetRequiredAsync(collectionId, cancellationToken);
        var members = await repository.ListAssetCollectionMembersAsync(
            collectionId,
            cancellationToken);
        var membersByAssetId = members.ToDictionary(member => member.Asset.AssetId);
        var missingAssetIds = requestedIds
            .Where(assetId => !membersByAssetId.ContainsKey(assetId))
            .ToArray();
        if (missingAssetIds.Length > 0)
        {
            throw new InvalidOperationException(
                $"只有项目内资产可以同步到 OSS；有 {missingAssetIds.Length:N0} 个所选资产不属于项目“{collection.Name}”。");
        }

        return new AssetCollectionSyncPlan(
            collection,
            requestedIds.Select(assetId => membersByAssetId[assetId]).ToArray());
    }

    private async Task<AssetCollection> GetRequiredAsync(
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        return await repository.GetAssetCollectionAsync(collectionId, cancellationToken)
            ?? throw new KeyNotFoundException("资产清单不存在或已被移除。");
    }

    private static string ValidateDetails(string name, AssetCollectionType type)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
        {
            throw new ArgumentException("项目名称不能为空。", nameof(name));
        }

        if (normalizedName.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"项目名称不能超过 {MaximumNameLength} 个字符。",
                nameof(name));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        return normalizedName;
    }
}

public sealed record AssetCollectionSyncPlan(
    AssetCollection Collection,
    IReadOnlyList<AssetCollectionMember> Members)
{
    public IReadOnlyList<AssetListItem> Assets =>
        Members.Select(member => member.Asset).ToArray();

    public int UnavailableAssetCount => Members.Count(member =>
        member.Asset.LocationStatus != AssetLocationStatus.Available);
}
