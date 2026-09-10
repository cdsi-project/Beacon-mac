using CDSI.Agent.Core.Assets;

namespace CDSI.Agent.Core.Scanning;

public sealed record ScanRoot(
    Guid Id,
    string Path,
    ScanRootMode Mode,
    bool Enabled,
    ScanRootStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastScannedAt,
    DateTimeOffset? RemovedAt,
    Guid? LocalVolumeId = null,
    string? VolumeRelativePath = null,
    AssetFileTypeFilter FileTypeFilter = AssetFileTypeFilter.All,
    IReadOnlyList<string>? ExtensionWhitelist = null,
    IReadOnlyList<AssetFileTypeFilter>? FileTypeFilters = null,
    IdleScanSchedule? IdleSchedule = null)
{
    public ScanFileFilter CreateFileFilter() =>
        FileTypeFilters is null
            ? new ScanFileFilter(FileTypeFilter, ExtensionWhitelist)
            : new ScanFileFilter(FileTypeFilters, ExtensionWhitelist);

    public IdleScanSchedule GetIdleScanSchedule() =>
        IdleSchedule ?? IdleScanSchedule.Disabled;
}

public enum ScanRootMode
{
    Readonly,
    Managed
}

public enum ScanRootStatus
{
    Active,
    Disabled,
    Unavailable,
    Offline,
    Error,
    Removed
}
