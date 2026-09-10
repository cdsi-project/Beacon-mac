namespace CDSI.Agent.Core.Scanning;

public sealed record DiscoveredFile(
    string FullPath,
    string OriginalFilename,
    string Extension,
    string? MimeType,
    long Size,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt);

public sealed record ScanError(string? Path, string Message);
