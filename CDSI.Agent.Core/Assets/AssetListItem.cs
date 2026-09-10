using CDSI.Agent.Core.Metadata;

namespace CDSI.Agent.Core.Assets;

public sealed record AssetListItem(
    Guid AssetId,
    string OriginalFilename,
    string Extension,
    string? MimeType,
    long Size,
    string? Sha256,
    DateTimeOffset ModifiedAt,
    DateTimeOffset DiscoveredAt,
    string Path,
    AssetLocationOwnership LocationOwnership,
    AssetLocationStatus LocationStatus,
    AssetStatus Status,
    bool HasHealthyObjectStorageBackup,
    AssetMetadata? Metadata = null)
{
    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> ProjectNames { get; init; } = [];

    public IReadOnlyList<string> HealthyBackupProviders { get; init; } = [];

    public DateTimeOffset? LatestHealthyBackupAt { get; init; }
}
