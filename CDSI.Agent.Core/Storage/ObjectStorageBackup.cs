namespace CDSI.Agent.Core.Storage;

public sealed class ObjectStorageConnection
{
    public ObjectStorageConnection(
        ObjectStorageProfile profile,
        string accessKeySecret,
        string? securityToken = null)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKeySecret);
        AccessKeySecret = accessKeySecret;
        SecurityToken = securityToken;
    }

    public ObjectStorageProfile Profile { get; }

    public string AccessKeySecret { get; }

    public string? SecurityToken { get; }

    public override string ToString()
    {
        return $"{Profile.Provider}:{Profile.DisplayName} (credentials redacted)";
    }
}

public sealed record ObjectStorageBackupRequest(
    Guid AssetId,
    string SourcePath,
    string? ObjectName = null,
    string? ObjectDirectory = null);

public enum UploadJobStatus
{
    Pending,
    Uploading,
    Verifying,
    Completed,
    PartiallyCompleted,
    Failed,
    Cancelled
}

public enum UploadItemStatus
{
    Pending,
    Uploading,
    Verifying,
    Completed,
    Failed,
    Cancelled
}

public enum StorageVerificationStatus
{
    Healthy,
    Missing,
    SizeMismatch,
    ChecksumMismatch,
    Unverified
}

public sealed record ObjectStorageUploadJob(
    Guid Id,
    Guid StorageProfileId,
    UploadJobStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int TotalItems,
    int CompletedItems,
    int FailedItems,
    long TotalBytes,
    long UploadedBytes,
    string? ErrorMessage);

public sealed record ObjectStorageUploadItem(
    Guid Id,
    Guid JobId,
    Guid AssetId,
    string SourcePath,
    string ObjectKey,
    UploadItemStatus Status,
    long Size,
    long UploadedBytes,
    string? ETag,
    string? ErrorMessage,
    DateTimeOffset? FinishedAt);

public sealed record ObjectStorageUploadAudit(
    ObjectStorageUploadJob Job,
    IReadOnlyList<ObjectStorageUploadItem> Items);

public sealed record MultipartUploadPart(
    long PartNumber,
    string ETag,
    long Size);

public sealed record MultipartUploadSession(
    Guid StorageProfileId,
    Guid AssetId,
    string ObjectKey,
    string SourcePath,
    string UploadId,
    long PartSize,
    long SourceSize,
    DateTimeOffset SourceModifiedAt,
    IReadOnlyList<MultipartUploadPart> Parts,
    DateTimeOffset UpdatedAt);

public sealed record ObjectStorageTransferRequest(
    ObjectStorageConnection Connection,
    Guid AssetId,
    string SourcePath,
    string ObjectKey,
    long Size,
    DateTimeOffset ModifiedAt,
    string Sha256,
    MultipartUploadSession? Session);

public sealed record ObjectStorageTransferProgress(
    long TransferredBytes,
    long CurrentRunTransferredBytes,
    long TotalBytes,
    int CompletedParts,
    int TotalParts,
    string? Message);

public sealed record ObjectStorageObjectInfo(
    string ObjectKey,
    long Size,
    string? Sha256,
    string? ETag,
    DateTimeOffset? LastModified);

public sealed record ObjectStorageTransferResult(
    ObjectStorageObjectInfo Object,
    bool Uploaded);

public sealed record ObjectStorageCopyRequest(
    ObjectStorageConnection Connection,
    Guid AssetId,
    string SourceObjectKey,
    string DestinationObjectKey,
    long Size,
    string? Sha256,
    string? SourceETag);

public sealed record ObjectStorageLocation(
    Guid Id,
    Guid AssetId,
    Guid StorageProfileId,
    string ObjectKey,
    StorageVerificationStatus Status,
    long Size,
    string? Sha256,
    string? ETag,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastVerifiedAt);

public sealed record ObjectStorageBackupProgress(
    Guid JobId,
    int TotalItems,
    int ProcessedItems,
    long TotalBytes,
    long UploadedBytes,
    long NetworkTransferredBytes,
    string? CurrentPath,
    string? Message);

public sealed record ObjectStorageBackupItemResult(
    Guid AssetId,
    string SourcePath,
    string ObjectKey,
    UploadItemStatus Status,
    long UploadedBytes,
    string? ErrorMessage);

public sealed record ObjectStorageBackupResult(
    Guid JobId,
    Guid StorageProfileId,
    UploadJobStatus Status,
    IReadOnlyList<ObjectStorageBackupItemResult> Items)
{
    public int CompletedItems => Items.Count(item =>
        item.Status == UploadItemStatus.Completed);

    public int FailedItems => Items.Count(item =>
        item.Status == UploadItemStatus.Failed);
}
