using CDSI.Agent.Core.Collections;

namespace CDSI.Agent.Core.Abstractions;

public interface IAssetCollectionRepository
{
    Task<bool> CreateAssetCollectionAsync(
        AssetCollection collection,
        CancellationToken cancellationToken = default);

    Task<AssetCollection?> GetAssetCollectionAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAssetCollectionAsync(
        AssetCollection collection,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetCollectionSummary>> ListAssetCollectionsAsync(
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAssetCollectionAsync(
        Guid collectionId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default);

    Task<int> AddAssetsToCollectionAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> assetIds,
        DateTimeOffset addedAt,
        CancellationToken cancellationToken = default);

    Task<int> RemoveAssetsFromCollectionAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> assetIds,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetCollectionMember>> ListAssetCollectionMembersAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default);
}
