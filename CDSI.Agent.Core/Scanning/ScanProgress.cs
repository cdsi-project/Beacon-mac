namespace CDSI.Agent.Core.Scanning;

public sealed record ScanProgress(
    ScanStage Stage,
    int FilesDiscovered,
    int FilesIndexed,
    int Errors,
    string? CurrentPath,
    string? Message = null);

public enum ScanStage
{
    Initializing,
    Discovering,
    Indexing,
    Completed,
    Cancelled,
    Failed
}

public sealed record ScanSummary(
    Guid JobId,
    ScanJobStatus Status,
    int FilesDiscovered,
    int FilesIndexed,
    int Errors);

public sealed record ScanBatchSummary(
    int RootsConfigured,
    int RootsScanned,
    int RootsUnavailable,
    int RootsFailed,
    int FilesDiscovered,
    int FilesIndexed,
    int Errors,
    bool Cancelled);
