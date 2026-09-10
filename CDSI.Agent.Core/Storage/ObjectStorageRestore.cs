namespace CDSI.Agent.Core.Storage;

public enum ObjectStorageRestoreDestinationKind
{
    ManagedWorkspace,
    SelectedDirectory
}

public enum RestoreJobStatus
{
    Pending,
    Downloading,
    Verifying,
    Completed,
    PartiallyCompleted,
    Failed,
    Cancelled
}

public enum RestoreItemStatus
{
    Pending,
    Downloading,
    Verifying,
    Completed,
    Failed,
    Cancelled
}

public sealed record ObjectStorageRestoreDestination(
    ObjectStorageRestoreDestinationKind Kind,
    string? DirectoryPath = null);

public sealed record ObjectStorageRestoreRequest(
    Guid AssetId,
    Guid StorageLocationId);

public sealed record ObjectStorageRestoreSource(
    Guid AssetId,
    string OriginalFilename,
    long AssetSize,
    DateTimeOffset AssetModifiedAt,
    string? AssetSha256,
    ObjectStorageLocation Location,
    string? LocalPath = null)
{
    public IReadOnlyList<string> ProjectNames { get; init; } = [];
}

public sealed record ConfiguredObjectStorageRestoreSource(
    ObjectStorageRestoreSource Source,
    ObjectStorageProfile Profile,
    bool HasStoredSecret);

public sealed record ObjectStorageRestoreCandidate(
    Guid AssetId,
    string OriginalFilename,
    IReadOnlyList<ConfiguredObjectStorageRestoreSource> Sources);

public sealed record ObjectStorageDownloadProgress(
    long TransferredBytes,
    long TotalBytes,
    string? Message);

public sealed record ObjectStorageDownloadResult(
    ObjectStorageObjectInfo Object,
    long DownloadedBytes);

public sealed record ObjectStorageRestoreJob(
    Guid Id,
    RestoreJobStatus Status,
    ObjectStorageRestoreDestinationKind DestinationKind,
    string TargetDirectory,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int TotalItems,
    int CompletedItems,
    int FailedItems,
    long TotalBytes,
    long DownloadedBytes,
    string? ErrorMessage);

public sealed record ObjectStorageRestoreItem(
    Guid Id,
    Guid JobId,
    Guid AssetId,
    Guid StorageProfileId,
    string ObjectKey,
    string TargetPath,
    RestoreItemStatus Status,
    long Size,
    long DownloadedBytes,
    string? Sha256,
    string? ErrorMessage,
    DateTimeOffset? FinishedAt);

public sealed record ObjectStorageRestoreAudit(
    ObjectStorageRestoreJob Job,
    IReadOnlyList<ObjectStorageRestoreItem> Items);

public sealed record ObjectStorageRestoreProgress(
    Guid JobId,
    int TotalItems,
    int ProcessedItems,
    long TotalBytes,
    long RestoredBytes,
    long NetworkTransferredBytes,
    string? CurrentPath,
    string? Message);

public sealed record ObjectStorageRestoreItemResult(
    Guid AssetId,
    string ObjectKey,
    string TargetPath,
    RestoreItemStatus Status,
    long DownloadedBytes,
    string? ErrorMessage);

public sealed record ObjectStorageRestoreResult(
    Guid JobId,
    RestoreJobStatus Status,
    IReadOnlyList<ObjectStorageRestoreItemResult> Items)
{
    public int CompletedItems => Items.Count(item =>
        item.Status == RestoreItemStatus.Completed);

    public int FailedItems => Items.Count(item =>
        item.Status == RestoreItemStatus.Failed);
}
