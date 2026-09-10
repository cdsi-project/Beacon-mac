using CDSI.Agent.Core.Assets;

namespace CDSI.Agent.Core.Duplicates;

public sealed record ExactDuplicateGroup(
    string Sha256,
    long Size,
    IReadOnlyList<DuplicateAssetItem> Assets);

public sealed record DuplicateAssetItem(
    Guid AssetId,
    string OriginalFilename,
    string Path,
    DateTimeOffset ModifiedAt,
    AssetLocationStatus LocationStatus);
