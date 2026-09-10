namespace CDSI.Agent.Core.Assets;

public sealed record AssetTag(
    Guid Id,
    string Name,
    string NormalizedName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AssetTagSummary(
    Guid Id,
    string Name,
    int AssetCount);
