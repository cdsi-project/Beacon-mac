namespace CDSI.Agent.Core.Assets;

public sealed record AssetStatistics(
    long AssetCount,
    long AvailableLocalFileCount,
    long UnavailableAssetCount,
    long TotalSizeBytes,
    long VideoAssetCount,
    long AudioAssetCount,
    long ImageAssetCount,
    long DocumentAssetCount,
    long OtherAssetCount,
    long BackedUpAssetCount,
    long VideoDurationMilliseconds)
{
    public long UnbackedUpAssetCount =>
        Math.Max(0, AssetCount - BackedUpAssetCount);
}
