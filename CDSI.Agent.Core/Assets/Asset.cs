namespace CDSI.Agent.Core.Assets;

public sealed record Asset(
    Guid Id,
    string OriginalFilename,
    string? MimeType,
    string Extension,
    long Size,
    string? Sha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset DiscoveredAt,
    AssetStatus Status);

public enum AssetStatus
{
    Discovered,
    Indexed,
    Error
}
