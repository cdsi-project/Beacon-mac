using CDSI.Agent.Core.Assets;

namespace CDSI.Agent.Core.Abstractions;

public interface IAssetTagRepository
{
    Task<IReadOnlyList<AssetTagSummary>> ListAssetTagsAsync(
        CancellationToken cancellationToken = default);

    Task<int> AssignAssetTagAsync(
        AssetTag tag,
        IReadOnlyCollection<Guid> assetIds,
        DateTimeOffset taggedAt,
        CancellationToken cancellationToken = default);

    Task<int> RemoveAssetTagAsync(
        Guid tagId,
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken cancellationToken = default);
}
