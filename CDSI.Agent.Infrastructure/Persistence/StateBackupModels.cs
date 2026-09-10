namespace CDSI.Agent.Infrastructure.Persistence;

public enum LocalStateBackupKind
{
    Manual,
    PreRestore
}

public enum LocalStateBackupStatus
{
    Restorable,
    Invalid,
    NewerVersion
}

public sealed record LocalStateBackupInfo(
    string Path,
    Guid? BackupId,
    DateTimeOffset? CreatedAtUtc,
    string? BeaconVersion,
    LocalStateBackupKind? Kind,
    long FileSize,
    LocalStateBackupStatus Status,
    string? Error,
    string? BundleSha256 = null);

public sealed record StateRestorePreparation(
    Guid RestoreId,
    LocalStateBackupInfo Backup,
    string SafetyBackupPath);

public sealed record StateRestoreApplyResult(
    Guid RestoreId,
    Guid BackupId,
    DateTimeOffset BackupCreatedAtUtc,
    string SafetyBackupPath);

internal sealed record StateBundleManifest(
    string Format,
    int FormatVersion,
    Guid BackupId,
    DateTimeOffset CreatedAtUtc,
    string BeaconVersion,
    string SourceClientId,
    string Platform,
    string Architecture,
    bool Encrypted,
    string BackupKind,
    IReadOnlyList<StateBundleDatabaseManifest> Databases);

internal sealed record StateBundleDatabaseManifest(
    string Role,
    string Path,
    bool Required,
    int SchemaVersion,
    long Size,
    string Sha256);

internal sealed record PendingStateRestorePlan(
    int FormatVersion,
    Guid RestoreId,
    DateTimeOffset PreparedAtUtc,
    string StagedBundleFileName,
    string BundleSha256,
    string SafetyKind,
    string? StagedSafetyBundleFileName,
    string? SafetyBundleSha256,
    string? RawSafetyManifestSha256,
    string SafetyBackupPath,
    string Phase);

internal sealed record RawStateSafetyManifest(
    int FormatVersion,
    Guid RestoreId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<RawStateSafetyFileManifest> Files);

internal sealed record RawStateSafetyFileManifest(
    string LogicalName,
    string FileName,
    bool Existed,
    long Size,
    string? Sha256);

internal sealed record ExtractedStateBundle(
    StateBundleManifest Manifest,
    string AssetDatabasePath,
    string ReaderDatabasePath);

public class StateBackupValidationException : IOException
{
    public StateBackupValidationException(string message)
        : base(message)
    {
    }

    public StateBackupValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class StateBackupNewerVersionException : StateBackupValidationException
{
    public StateBackupNewerVersionException(string message)
        : base(message)
    {
    }
}

public sealed class StateRestoreFailedException : InvalidOperationException
{
    public StateRestoreFailedException(
        string message,
        bool currentStateIsSafe,
        Exception innerException,
        string? safetyBackupPath = null)
        : base(message, innerException)
    {
        CurrentStateIsSafe = currentStateIsSafe;
        SafetyBackupPath = safetyBackupPath;
    }

    public bool CurrentStateIsSafe { get; }

    public string? SafetyBackupPath { get; }
}
