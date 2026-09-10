using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Core.Assets;

public sealed record RegisteredLocalAsset(
    Guid AssetId,
    DiscoveredFile File,
    bool RequiresFingerprint);
