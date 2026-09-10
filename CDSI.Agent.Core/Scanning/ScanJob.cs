namespace CDSI.Agent.Core.Scanning;

public sealed record ScanJob(
    Guid Id,
    Guid ScanRootId,
    ScanJobStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int FilesDiscovered,
    int FilesProcessed,
    int Errors,
    string? ErrorMessage);

public enum ScanJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}
