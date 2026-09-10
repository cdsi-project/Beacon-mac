using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Core.Collections;

public enum AssetCollectionType
{
    Video,
    Audio,
    Image,
    Text,
    Mixed
}

public sealed record AssetCollection(
    Guid Id,
    string Name,
    AssetCollectionType Type,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<Guid> BackupProfileIds { get; init; } = [];
}

public sealed record AssetCollectionSummary(
    Guid Id,
    string Name,
    AssetCollectionType Type,
    int AssetCount,
    long TotalSizeBytes,
    int BackedUpAssetCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public IReadOnlyList<AssetCollectionBackupTarget> BackupTargets { get; init; } = [];
}

public sealed record AssetCollectionBackupTarget(
    Guid ProfileId,
    string ProfileName,
    ObjectStorageProvider Provider);

public sealed record AssetCollectionMember(
    Guid CollectionId,
    AssetListItem Asset,
    DateTimeOffset AddedAt);
