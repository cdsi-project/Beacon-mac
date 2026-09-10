namespace CDSI.Agent.Core.Assets;

public sealed record AssetDirectorySummary(
    string Path,
    long AssetCount,
    long AvailableAssetCount,
    long MissingAssetCount,
    long AvailableSizeBytes,
    DateTimeOffset LatestModifiedAt);

public sealed record AssetDirectoryExclusionResult(
    string Path,
    int ExcludedLocationCount,
    int StoppedScanRootCount);
