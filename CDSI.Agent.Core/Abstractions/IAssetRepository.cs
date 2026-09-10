using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Duplicates;
using CDSI.Agent.Core.Fingerprints;
using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Transfers;
using CDSI.Agent.Core.Workspaces;

namespace CDSI.Agent.Core.Abstractions;

public interface IAssetRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<string> GetOrCreateDeviceIdAsync(CancellationToken cancellationToken = default);

    Task<ScanRoot> GetOrCreateScanRootAsync(
        string path,
        ScanRootMode mode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScanRoot>> ListScanRootsAsync(
        bool includeRemoved = false,
        CancellationToken cancellationToken = default);

    Task SetScanRootEnabledAsync(
        Guid scanRootId,
        bool enabled,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task SetScanRootFileFilterAsync(
        Guid scanRootId,
        ScanFileFilter fileFilter,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task SetScanRootIdleScheduleAsync(
        Guid scanRootId,
        IdleScanSchedule schedule,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task RemoveScanRootAsync(
        Guid scanRootId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task SetScanRootStatusAsync(
        Guid scanRootId,
        ScanRootStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<LocalVolumeReconciliationResult> ReconcileLocalVolumesAsync(
        IReadOnlyCollection<LocalVolumeDescriptor> mountedVolumes,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<ManagedWorkspace?> GetManagedWorkspaceAsync(
        string deviceId,
        CancellationToken cancellationToken = default);

    Task<ManagedWorkspace> SaveManagedWorkspaceAsync(
        string deviceId,
        string path,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task CreateScanJobAsync(ScanJob job, CancellationToken cancellationToken = default);

    Task UpdateScanJobAsync(ScanJob job, CancellationToken cancellationToken = default);

    Task MarkScanRootCompletedAsync(
        Guid scanRootId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    Task MarkMissingLocalLocationsAsync(
        string deviceId,
        string rootPath,
        DateTimeOffset scanStartedAt,
        ScanFileFilter? fileFilter = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegisteredLocalAsset>> RegisterLocalFilesAsync(
        string deviceId,
        IReadOnlyCollection<DiscoveredFile> files,
        DateTimeOffset discoveredAt,
        CancellationToken cancellationToken = default);

    Task<LocalAssetTransferSource?> GetLocalAssetTransferSourceAsync(
        Guid assetId,
        string deviceId,
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task RegisterManagedLocalLocationAsync(
        Guid assetId,
        string deviceId,
        string path,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default);

    Task RegisterLocalLocationAsync(
        Guid assetId,
        string deviceId,
        string path,
        AssetLocationOwnership ownership,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default);

    Task MarkLocalLocationMissingAsync(
        string deviceId,
        string path,
        DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default);

    Task CreateFileOperationAsync(
        FileOperationRecord operation,
        IReadOnlyCollection<FileOperationItemRecord> items,
        CancellationToken cancellationToken = default);

    Task SaveFileOperationItemAsync(
        FileOperationItemRecord item,
        CancellationToken cancellationToken = default);

    Task UpdateFileOperationAsync(
        FileOperationRecord operation,
        CancellationToken cancellationToken = default);

    Task<FileOperationAudit?> GetFileOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
    Task<bool> SaveSha256Async(
        Guid assetId,
        long expectedSize,
        DateTimeOffset expectedModifiedAt,
        string sha256,
        CancellationToken cancellationToken = default);

    Task<FingerprintWorkSummary> GetFingerprintWorkSummaryAsync(
        FingerprintMode mode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FingerprintCandidate>> ListFingerprintCandidatesAsync(
        FingerprintMode mode,
        Guid? afterAssetId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<MetadataWorkSummary> GetMetadataWorkSummaryAsync(
        int pipelineVersion,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MetadataCandidate>> ListMetadataCandidatesAsync(
        int pipelineVersion,
        Guid? afterAssetId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<bool> SaveMetadataAsync(
        AssetMetadata metadata,
        CancellationToken cancellationToken = default);

    Task<AssetMetadata?> GetMetadataAsync(
        Guid assetId,
        CancellationToken cancellationToken = default);

    Task<AssetStatistics> GetLocalAssetStatisticsAsync(
        CancellationToken cancellationToken = default);

    Task<long> GetAssetListCountAsync(
        CancellationToken cancellationToken = default);

    Task<long> GetAssetListCountAsync(
        AssetListFilter filter,
        CancellationToken cancellationToken = default);

    Task<int> HideAssetsFromListAsync(
        IReadOnlyCollection<Guid> assetIds,
        DateTimeOffset hiddenAt,
        CancellationToken cancellationToken = default);

    Task<AssetDirectoryExclusionResult> ExcludeAssetDirectoryAsync(
        string path,
        DateTimeOffset excludedAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListExcludedAssetDirectoryPathsAsync(
        CancellationToken cancellationToken = default);

    Task RestoreAssetDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetDirectorySummary>> ListAssetDirectoriesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListAssetExtensionsAsync(
        AssetFileTypeFilter fileType = AssetFileTypeFilter.All,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetListItem>> ListAssetsAsync(
        int limit,
        long offset = 0,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetListItem>> ListAssetsAsync(
        AssetListFilter filter,
        int limit,
        long offset = 0,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExactDuplicateGroup>> ListExactDuplicateGroupsAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
