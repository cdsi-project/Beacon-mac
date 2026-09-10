using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Core.Fingerprints;

public sealed record FileFingerprint(
    string Sha256,
    long Size,
    DateTimeOffset ModifiedAt);

public sealed record FileHashProgress(
    long BytesProcessed,
    long TotalBytes);

public sealed class FileChangedDuringFingerprintException : IOException
{
    public FileChangedDuringFingerprintException(DiscoveredFile file)
        : base($"File changed while its fingerprint was being calculated: {file.FullPath}")
    {
        FilePath = file.FullPath;
    }

    public string FilePath { get; }
}
