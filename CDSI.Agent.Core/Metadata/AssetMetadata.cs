using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Core.Metadata;

public static class MetadataPipeline
{
    public const int CurrentVersion = 1;
}

public enum MetadataExtractionStatus
{
    Extracted,
    Unsupported,
    Error
}

public enum AssetMediaKind
{
    Generic,
    Image,
    Audio,
    Video
}

public sealed record AssetMetadataContent(
    AssetMediaKind Kind,
    int? Width = null,
    int? Height = null,
    long? DurationMilliseconds = null,
    string? VideoCodec = null,
    int? VideoBitrateKbps = null,
    string? AudioCodec = null,
    int? AudioBitrateKbps = null,
    int? AudioSampleRateHz = null,
    int? AudioChannels = null,
    string? Title = null,
    string? Artist = null,
    string? Album = null);

public sealed record AssetMetadata(
    Guid AssetId,
    string ExtractorName,
    int PipelineVersion,
    MetadataExtractionStatus Status,
    long SourceSize,
    DateTimeOffset SourceModifiedAt,
    AssetMetadataContent? Content,
    DateTimeOffset ExtractedAt,
    string? ErrorMessage);

public sealed record MetadataCandidate(
    Guid AssetId,
    DiscoveredFile File);

public sealed record MetadataWorkSummary(int Files);

public sealed record MetadataExtractionResult(
    MetadataExtractionStatus Status,
    AssetMetadataContent? Content = null);

public sealed record MetadataProgress(
    int TotalFiles,
    int CompletedFiles,
    int ExtractedFiles,
    int UnsupportedFiles,
    int Errors,
    string? CurrentPath,
    string? Message = null);

public sealed record MetadataSummary(
    int TotalFiles,
    int ExtractedFiles,
    int UnsupportedFiles,
    int Errors,
    bool Cancelled);

public sealed class FileChangedDuringMetadataExtractionException : IOException
{
    public FileChangedDuringMetadataExtractionException(DiscoveredFile file)
        : base($"File changed during metadata extraction: {file.FullPath}")
    {
    }
}
