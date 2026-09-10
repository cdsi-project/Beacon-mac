using CDSI.Agent.Infrastructure.Identity;
using CDSI.Agent.Infrastructure.Persistence;

namespace CDSI.Agent.Mac.Services;

[Flags]
internal enum MissingStateDatabases
{
    None = 0,
    Asset = 1,
    Reader = 2
}

internal sealed record ApplicationStartupState(
    MissingStateDatabases MissingDatabases,
    StateRestoreApplyResult? RestoreResult,
    string? RestoreWarning,
    string? RestoreSafetyBackupPath)
{
    public static ApplicationStartupState Empty { get; } = new(
        MissingStateDatabases.None,
        RestoreResult: null,
        RestoreWarning: null,
        RestoreSafetyBackupPath: null);
}

internal static class ApplicationStartupCoordinator
{
    public static async Task<ApplicationStartupState> PrepareAsync(
        string dataDirectory,
        RuntimeLogService runtimeLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(runtimeLog);

        var normalizedDataDirectory = Path.GetFullPath(dataDirectory);
        var assetDatabasePath = Path.Combine(normalizedDataDirectory, "cdsi.db");
        var readerDatabasePath = Path.Combine(normalizedDataDirectory, "reader.db");
        var identityExistedBeforeStartup = File.Exists(Path.Combine(
            normalizedDataDirectory,
            FileClientIdentityProvider.IdentityFileName));
        var pendingRestore = new PendingStateRestoreService(
            normalizedDataDirectory,
            assetDatabasePath,
            readerDatabasePath);

        StateRestoreApplyResult? restoreResult = null;
        string? restoreWarning = null;
        string? restoreSafetyBackupPath = null;
        try
        {
            restoreResult = await pendingRestore.ApplyPendingAsync(cancellationToken);
            if (restoreResult is not null)
            {
                runtimeLog.WriteInformation(
                    $"Beacon 状态恢复完成；RestoreId={restoreResult.RestoreId:D}；" +
                    $"BackupId={restoreResult.BackupId:D}");
            }
        }
        catch (StateRestoreFailedException exception) when (exception.CurrentStateIsSafe)
        {
            runtimeLog.WriteError("状态恢复未完成，已保留操作前状态", exception);
            restoreSafetyBackupPath = exception.SafetyBackupPath;
            restoreWarning =
                "状态恢复未完成，已自动保留操作前的状态。当前数据库未被更改。\n\n" +
                "请打开“工具 > 运行日志”查看详细原因。";
        }
        catch (StateBackupValidationException exception)
        {
            runtimeLog.WriteError("待恢复状态备份验证失败", exception);
            restoreWarning =
                $"待恢复的状态备份未通过验证，当前数据未更改。\n\n{exception.Message}";
        }

        var missingDatabases = GetMissingStateDatabases(
            identityExistedBeforeStartup,
            File.Exists(assetDatabasePath),
            File.Exists(readerDatabasePath));
        return new ApplicationStartupState(
            missingDatabases,
            restoreResult,
            restoreWarning,
            restoreSafetyBackupPath);
    }

    internal static MissingStateDatabases GetMissingStateDatabases(
        bool clientIdentityExistedBeforeStartup,
        bool assetDatabaseExists,
        bool readerDatabaseExists)
    {
        var existingInstallation =
            clientIdentityExistedBeforeStartup ||
            assetDatabaseExists ||
            readerDatabaseExists;
        if (!existingInstallation)
        {
            return MissingStateDatabases.None;
        }

        var missing = MissingStateDatabases.None;
        if (!assetDatabaseExists)
        {
            missing |= MissingStateDatabases.Asset;
        }

        if (!readerDatabaseExists)
        {
            missing |= MissingStateDatabases.Reader;
        }

        return missing;
    }
}
