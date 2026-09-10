namespace CDSI.Agent.Core.Scanning;

public sealed record LocalVolumeDescriptor(
    string StableId,
    string SerialNumber,
    string MountPath,
    string? Label,
    string? FileSystem,
    string DriveType);

public sealed record LocalVolumeReconciliationResult(
    int MountedVolumes,
    int NewlyTrackedVolumes,
    int BoundScanRoots,
    int BoundAssetLocations,
    int RemappedScanRoots,
    int RemappedAssetLocations,
    int ReconnectedVolumes,
    int OfflineVolumes)
{
    public bool HasChanges =>
        NewlyTrackedVolumes > 0 ||
        BoundScanRoots > 0 ||
        BoundAssetLocations > 0 ||
        RemappedScanRoots > 0 ||
        RemappedAssetLocations > 0 ||
        ReconnectedVolumes > 0 ||
        OfflineVolumes > 0;
}
