namespace CDSI.Agent.Core.Transfers;

public enum ManagedAssetTransferAction
{
    Copy,
    Move
}

public enum FileOperationStatus
{
    Pending,
    Running,
    Completed,
    PartiallyCompleted,
    Failed,
    Cancelled
}

public enum FileOperationItemStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled
}

public sealed record ManagedAssetTransferRequest(
    Guid AssetId,
    string SourcePath);

public sealed record LocalAssetTransferSource(
    Guid AssetId,
    string OriginalFilename,
    string Extension,
    long Size,
    DateTimeOffset ModifiedAt,
    string? Sha256,
    string Path);

public sealed record VerifiedManagedFileCopy(
    long Size,
    string Sha256,
    bool TargetAlreadyExisted);

public sealed record ManagedAssetTransferProgress(
    Guid OperationId,
    int TotalItems,
    int ProcessedItems,
    long TotalBytes,
    long ProcessedBytes,
    string? CurrentPath,
    string? Message);

public sealed record ManagedAssetTransferItemResult(
    Guid AssetId,
    string SourcePath,
    string? TargetPath,
    FileOperationItemStatus Status,
    bool SourceDeleted,
    string? ErrorMessage);

public sealed record ManagedAssetTransferResult(
    Guid OperationId,
    ManagedAssetTransferAction Action,
    FileOperationStatus Status,
    IReadOnlyList<ManagedAssetTransferItemResult> Items)
{
    public int CompletedItems => Items.Count(item =>
        item.Status == FileOperationItemStatus.Completed);

    public int FailedItems => Items.Count(item =>
        item.Status == FileOperationItemStatus.Failed);
}

public sealed record FileOperationRecord(
    Guid Id,
    ManagedAssetTransferAction Action,
    FileOperationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int TotalItems,
    int CompletedItems,
    int FailedItems,
    string? ErrorMessage);

public sealed record FileOperationItemRecord(
    Guid Id,
    Guid OperationId,
    Guid AssetId,
    string SourcePath,
    string? TargetPath,
    FileOperationItemStatus Status,
    bool SourceDeleted,
    string? Sha256,
    string? ErrorMessage,
    DateTimeOffset? FinishedAt);

public sealed record FileOperationAudit(
    FileOperationRecord Operation,
    IReadOnlyList<FileOperationItemRecord> Items);
