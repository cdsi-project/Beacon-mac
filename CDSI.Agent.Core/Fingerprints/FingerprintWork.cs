using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Core.Fingerprints;

public enum FingerprintMode
{
    DuplicateCandidates,
    Complete
}

public sealed record FingerprintCandidate(
    Guid AssetId,
    DiscoveredFile File);

public sealed record FingerprintWorkSummary(
    int Files,
    long Bytes);

public sealed record FingerprintProgress(
    FingerprintMode Mode,
    int TotalFiles,
    int CompletedFiles,
    int FingerprintedFiles,
    int Errors,
    long TotalBytes,
    long ProcessedBytes,
    double BytesPerSecond,
    string? CurrentPath,
    string? Message = null);

public sealed record FingerprintSummary(
    FingerprintMode Mode,
    int TotalFiles,
    int FingerprintedFiles,
    int Errors,
    long ProcessedBytes,
    bool Cancelled);
