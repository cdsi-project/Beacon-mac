using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed class PendingStateRestoreService
{
    private const string ApplyingPhase = "Applying";
    private const string SafetyCapturedPhase = "SafetyCaptured";
    private const string AssetAppliedPhase = "AssetApplied";
    private const string ReaderAppliedPhase = "ReaderApplied";
    private const string VerifyingPhase = "Verifying";
    private const string CompletedPhase = "Completed";
    private const string RolledBackPhase = "RolledBack";
    private const string AbandonedPhase = "Abandoned";
    private const string RollbackFailedPhase = "RollbackFailed";
    private const string RawSafetyManifestFileName = "raw-safety.json";

    private readonly string _dataDirectory;
    private readonly string _assetDatabasePath;
    private readonly string _readerDatabasePath;
    private readonly string _protectionRoot;
    private readonly string _pendingPlanPath;
    private readonly Action<string>? _beforeReplace;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly HashSet<Guid> _validatedRawSafetyRestoreIds = [];

    public PendingStateRestoreService(
        string dataDirectory,
        string assetDatabasePath,
        string readerDatabasePath)
        : this(
            dataDirectory,
            assetDatabasePath,
            readerDatabasePath,
            beforeReplace: null)
    {
    }

    internal PendingStateRestoreService(
        string dataDirectory,
        string assetDatabasePath,
        string readerDatabasePath,
        Action<string>? beforeReplace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(readerDatabasePath);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _assetDatabasePath = Path.GetFullPath(assetDatabasePath);
        _readerDatabasePath = Path.GetFullPath(readerDatabasePath);
        if (PathsEqual(_assetDatabasePath, _readerDatabasePath))
        {
            throw new ArgumentException("资产数据库和 RSS订阅数据库不能使用同一路径。");
        }

        _protectionRoot = LocalStateProtectionService.GetProtectionRoot(dataDirectory);
        _pendingPlanPath = Path.Combine(
            _protectionRoot,
            LocalStateProtectionService.PendingPlanFileName);
        _beforeReplace = beforeReplace;
    }

    public bool HasPendingRestore =>
        StateProtectionPathGuard.TryGetAttributes(_pendingPlanPath, out _);

    public async Task<StateRestoreApplyResult?> ApplyPendingAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _validatedRawSafetyRestoreIds.Clear();
            try
            {
                EnsureProtectionRootForStartup();
                ValidateKnownProtectionDirectories();
                CleanupOwnedStartupTemporaryFiles();
                TryCompleteDeferredCleanup();
            }
            catch (StateBackupValidationException exception)
            {
                throw new StateRestoreFailedException(
                    "状态保护目录验证失败，无法安全判断是否存在未完成的恢复。请停止使用 Beacon 并检查状态保护目录。",
                    currentStateIsSafe: false,
                    exception);
            }

            if (!StateProtectionPathGuard.TryGetAttributes(
                    _pendingPlanPath,
                    out _))
            {
                TryCleanupOrphanedWorkDirectories();
                return null;
            }

            PendingStateRestorePlan? plan = null;
            string operationDirectory;
            try
            {
                plan = await ReadAndValidatePlanAsync(cancellationToken);
                operationDirectory = GetValidatedOperationDirectory(plan);
            }
            catch (StateBackupValidationException exception)
            {
                throw new StateRestoreFailedException(
                    "挂起的状态恢复记录已损坏，无法确认两个数据库是否处于同一状态。请保留状态保护目录并停止使用 Beacon。",
                    currentStateIsSafe: false,
                    exception,
                    GetReportableSafetyPath(plan));
            }

            plan = plan ?? throw new InvalidOperationException("恢复计划未加载。");

            if (IsCleanupOnlyPhase(plan.Phase))
            {
                CompleteCleanup(operationDirectory, plan.RestoreId);
                return null;
            }

            if (!string.Equals(
                    plan.Phase,
                    LocalStateProtectionService.PreparedPhase,
                    StringComparison.Ordinal))
            {
                return await RecoverInterruptedRestoreAsync(
                    plan,
                    operationDirectory,
                    cancellationToken);
            }

            return await ApplyPreparedRestoreAsync(
                plan,
                operationDirectory,
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<StateRestoreApplyResult?> RecoverInterruptedRestoreAsync(
        PendingStateRestorePlan plan,
        string operationDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            CleanupDisposableExtractionDirectories(
                operationDirectory,
                preserveSafety: true);
            await RestoreSafetyAsync(
                plan,
                operationDirectory,
                cancellationToken,
                preparedBundleSafety: null);
            plan = plan with { Phase = RolledBackPhase };
            await WritePlanAsync(plan, CancellationToken.None);
            CompleteCleanup(operationDirectory, plan.RestoreId);
            throw new StateRestoreFailedException(
                "检测到上次未完成的状态恢复，Beacon 已回滚到恢复前状态。",
                currentStateIsSafe: true,
                new InvalidOperationException($"Interrupted restore phase: {plan.Phase}"),
                GetReportableSafetyPath(plan));
        }
        catch (StateRestoreFailedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await TryWritePhaseAsync(plan, RollbackFailedPhase);
            throw new StateRestoreFailedException(
                "上次状态恢复被中断，并且自动回滚失败。请保留状态保护目录并停止使用 Beacon。",
                currentStateIsSafe: false,
                exception,
                GetReportableSafetyPath(plan));
        }
    }

    private async Task<StateRestoreApplyResult> ApplyPreparedRestoreAsync(
        PendingStateRestorePlan initialPlan,
        string operationDirectory,
        CancellationToken cancellationToken)
    {
        var plan = initialPlan;
        var replacementStarted = false;
        var incomingDirectory = Path.Combine(operationDirectory, "incoming");
        StateBundleManifest? incomingManifest = null;
        ExtractedStateBundle? preparedBundleSafety = null;
        try
        {
            await VerifyStagedFileAsync(
                operationDirectory,
                plan.StagedBundleFileName,
                plan.BundleSha256,
                cancellationToken);
            ResetControlledDirectory(incomingDirectory);
            var incoming = await StateBundleArchive.ExtractAndValidateAsync(
                Path.Combine(operationDirectory, plan.StagedBundleFileName),
                incomingDirectory,
                cancellationToken);
            if (IsBundleSafety(plan))
            {
                await VerifyStagedFileAsync(
                    operationDirectory,
                    plan.StagedSafetyBundleFileName!,
                    plan.SafetyBundleSha256!,
                    cancellationToken);
                var safetyDirectory = Path.Combine(operationDirectory, "safety");
                ResetControlledDirectory(safetyDirectory);
                preparedBundleSafety = await StateBundleArchive.ExtractAndValidateAsync(
                    Path.Combine(operationDirectory, plan.StagedSafetyBundleFileName!),
                    safetyDirectory,
                    cancellationToken);
            }

            incomingManifest = incoming.Manifest;

            await UpgradePreparedDatabasesAsync(incoming, cancellationToken);
            await StateBundleArchive.ValidateSqliteDatabaseAsync(
                incoming.AssetDatabasePath,
                "asset",
                DatabaseMigrator.CurrentSchemaVersion,
                cancellationToken);
            await StateBundleArchive.ValidateSqliteDatabaseAsync(
                incoming.ReaderDatabasePath,
                "reader",
                Reader.ReaderDatabaseMigrator.CurrentSchemaVersion,
                cancellationToken);

            if (IsRawFilesSafety(plan))
            {
                plan = await CaptureRawSafetyAsync(plan, cancellationToken);
            }

            plan = plan with { Phase = ApplyingPhase };
            await WritePlanAsync(plan, cancellationToken);
            replacementStarted = true;
            SqliteConnection.ClearAllPools();
            ReplaceDatabase(
                incoming.AssetDatabasePath,
                _assetDatabasePath,
                Path.Combine(operationDirectory, "displaced-cdsi.db"),
                "asset",
                invokeFaultHook: true);

            plan = plan with { Phase = AssetAppliedPhase };
            await WritePlanAsync(plan, cancellationToken);
            ReplaceDatabase(
                incoming.ReaderDatabasePath,
                _readerDatabasePath,
                Path.Combine(operationDirectory, "displaced-reader.db"),
                "reader",
                invokeFaultHook: true);

            plan = plan with { Phase = ReaderAppliedPhase };
            await WritePlanAsync(plan, cancellationToken);
            plan = plan with { Phase = VerifyingPhase };
            await WritePlanAsync(plan, cancellationToken);
            await StateBundleArchive.ValidateSqliteDatabaseAsync(
                _assetDatabasePath,
                "asset",
                DatabaseMigrator.CurrentSchemaVersion,
                cancellationToken);
            await StateBundleArchive.ValidateSqliteDatabaseAsync(
                _readerDatabasePath,
                "reader",
                Reader.ReaderDatabaseMigrator.CurrentSchemaVersion,
                cancellationToken);

            plan = plan with { Phase = CompletedPhase };
            await WritePlanAsync(plan, cancellationToken);
            var result = new StateRestoreApplyResult(
                plan.RestoreId,
                incomingManifest.BackupId,
                incomingManifest.CreatedAtUtc,
                plan.SafetyBackupPath);
            CompleteCleanup(operationDirectory, plan.RestoreId);
            return result;
        }
        catch (Exception exception)
        {
            if (!replacementStarted)
            {
                try
                {
                    plan = plan with { Phase = AbandonedPhase };
                    await WritePlanAsync(plan, CancellationToken.None);
                    CompleteCleanup(operationDirectory, plan.RestoreId);
                }
                catch (Exception terminalStateException)
                {
                    throw new StateRestoreFailedException(
                        "状态备份验证或迁移失败，并且无法持久记录放弃状态。为避免下次启动意外重放恢复，Beacon 必须停止使用。",
                        currentStateIsSafe: false,
                        new AggregateException(exception, terminalStateException),
                        GetReportableSafetyPath(plan));
                }

                throw new StateRestoreFailedException(
                    "状态备份验证或迁移失败，当前 Beacon 数据未被修改。",
                    currentStateIsSafe: true,
                    exception,
                    GetReportableSafetyPath(plan));
            }

            try
            {
                await RestoreSafetyAsync(
                    plan,
                    operationDirectory,
                    CancellationToken.None,
                    preparedBundleSafety);
                plan = plan with { Phase = RolledBackPhase };
                await WritePlanAsync(plan, CancellationToken.None);
                CompleteCleanup(operationDirectory, plan.RestoreId);
                throw new StateRestoreFailedException(
                    "状态恢复失败，Beacon 已自动回滚到恢复前状态。",
                    currentStateIsSafe: true,
                    exception,
                    GetReportableSafetyPath(plan));
            }
            catch (StateRestoreFailedException)
            {
                throw;
            }
            catch (Exception rollbackException)
            {
                await TryWritePhaseAsync(plan, RollbackFailedPhase);
                throw new StateRestoreFailedException(
                    "状态恢复及自动回滚均失败。请保留状态保护目录并停止使用 Beacon。",
                    currentStateIsSafe: false,
                    new AggregateException(exception, rollbackException),
                    GetReportableSafetyPath(plan));
            }
        }
    }

    private Task RestoreSafetyAsync(
        PendingStateRestorePlan plan,
        string operationDirectory,
        CancellationToken cancellationToken,
        ExtractedStateBundle? preparedBundleSafety)
    {
        return IsRawFilesSafety(plan)
            ? RestoreRawSafetyAsync(plan, cancellationToken)
            : RestoreSafetyBundleAsync(
                plan,
                operationDirectory,
                preparedBundleSafety,
                cancellationToken);
    }

    private async Task RestoreSafetyBundleAsync(
        PendingStateRestorePlan plan,
        string operationDirectory,
        ExtractedStateBundle? preparedSafety,
        CancellationToken cancellationToken)
    {
        var safety = preparedSafety;
        if (safety is null)
        {
            await VerifyStagedFileAsync(
                operationDirectory,
                plan.StagedSafetyBundleFileName!,
                plan.SafetyBundleSha256!,
                cancellationToken);
            var rollbackDirectory = Path.Combine(operationDirectory, "rollback");
            ResetControlledDirectory(rollbackDirectory);
            safety = await StateBundleArchive.ExtractAndValidateAsync(
                Path.Combine(operationDirectory, plan.StagedSafetyBundleFileName!),
                rollbackDirectory,
                cancellationToken);
        }

        SqliteConnection.ClearAllPools();
        ReplaceDatabase(
            safety.AssetDatabasePath,
            _assetDatabasePath,
            Path.Combine(operationDirectory, "failed-restored-cdsi.db"),
            "asset",
            invokeFaultHook: false);
        ReplaceDatabase(
            safety.ReaderDatabasePath,
            _readerDatabasePath,
            Path.Combine(operationDirectory, "failed-restored-reader.db"),
            "reader",
            invokeFaultHook: false);
        await StateBundleArchive.ValidateSqliteDatabaseAsync(
            _assetDatabasePath,
            "asset",
            expectedSchemaVersion: null,
            cancellationToken);
        await StateBundleArchive.ValidateSqliteDatabaseAsync(
            _readerDatabasePath,
            "reader",
            expectedSchemaVersion: null,
            cancellationToken);
    }

    private async Task<PendingStateRestorePlan> CaptureRawSafetyAsync(
        PendingStateRestorePlan plan,
        CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        var emergencyRoot = StateProtectionPathGuard.EnsureDirectory(
            _protectionRoot,
            Path.Combine(
                _protectionRoot,
                LocalStateProtectionService.EmergencySafetyDirectoryName));
        var safetyDirectory = LocalStateProtectionService.GetEmergencySafetyDirectory(
            _protectionRoot,
            plan.RestoreId);
        if (!StateProtectionPathGuard.PathsEqual(
                safetyDirectory,
                plan.SafetyBackupPath))
        {
            throw new StateBackupValidationException(
                "紧急恢复安全目录与恢复计划不一致。");
        }

        var planPublished = false;
        try
        {
            StateProtectionPathGuard.ResetDirectory(emergencyRoot, safetyDirectory);
            var files = new List<RawStateSafetyFileManifest>();
            foreach (var specification in GetRawSafetyFileSpecifications())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!StateProtectionPathGuard.TryGetAttributes(
                        specification.SourcePath,
                        out var sourceAttributes))
                {
                    files.Add(new RawStateSafetyFileManifest(
                        specification.LogicalName,
                        specification.StoredFileName,
                        Existed: false,
                        Size: 0,
                        Sha256: null));
                    continue;
                }

                if ((sourceAttributes & FileAttributes.Directory) != 0 ||
                    (sourceAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StateBackupValidationException(
                        "当前 SQLite 文件族包含符号链接，无法创建紧急恢复安全副本。");
                }

                var destination = Path.Combine(
                    safetyDirectory,
                    specification.StoredFileName);
                await CopyRawFileAsync(
                    specification.SourcePath,
                    destination,
                    cancellationToken);
                var size = new FileInfo(destination).Length;
                var sha256 = await StateBundleArchive.ComputeSha256Async(
                    destination,
                    cancellationToken);
                files.Add(new RawStateSafetyFileManifest(
                    specification.LogicalName,
                    specification.StoredFileName,
                    Existed: true,
                    size,
                    sha256));
            }

            var manifest = new RawStateSafetyManifest(
                StateBundleArchive.CurrentFormatVersion,
                plan.RestoreId,
                DateTimeOffset.UtcNow,
                files);
            var manifestPath = Path.Combine(safetyDirectory, RawSafetyManifestFileName);
            await WriteJsonFileAtomicallyAsync(
                manifestPath,
                manifest,
                overwrite: false,
                cancellationToken);
            var manifestSha256 = await StateBundleArchive.ComputeSha256Async(
                manifestPath,
                cancellationToken);
            var capturedPlan = plan with
            {
                RawSafetyManifestSha256 = manifestSha256,
                Phase = SafetyCapturedPhase
            };
            _ = await ReadAndValidateRawSafetyAsync(capturedPlan, cancellationToken);
            await WritePlanAsync(capturedPlan, cancellationToken);
            planPublished = true;
            return capturedPlan;
        }
        finally
        {
            if (!planPublished)
            {
                _ = StateProtectionPathGuard.TryDeleteDirectory(
                    emergencyRoot,
                    safetyDirectory);
            }
        }
    }

    private async Task RestoreRawSafetyAsync(
        PendingStateRestorePlan plan,
        CancellationToken cancellationToken)
    {
        var rawSafety = await ReadAndValidateRawSafetyAsync(
            plan,
            cancellationToken);
        var files = rawSafety.Files.ToDictionary(
            file => file.LogicalName,
            StringComparer.Ordinal);

        SqliteConnection.ClearAllPools();
        foreach (var specification in GetRawSafetyFileSpecifications())
        {
            var file = files[specification.LogicalName];
            if (file.Existed)
            {
                await RestoreRawFileAtomicallyAsync(
                    Path.Combine(plan.SafetyBackupPath, file.FileName),
                    specification.SourcePath,
                    cancellationToken);
            }
            else
            {
                File.Delete(specification.SourcePath);
            }
        }

        foreach (var specification in GetRawSafetyFileSpecifications())
        {
            var file = files[specification.LogicalName];
            if (!file.Existed)
            {
                if (StateProtectionPathGuard.TryGetAttributes(
                        specification.SourcePath,
                        out _))
                {
                    throw new StateBackupValidationException(
                        "紧急恢复未能还原 SQLite 文件缺失状态。");
                }

                continue;
            }

            if (!StateProtectionPathGuard.TryGetAttributes(
                    specification.SourcePath,
                    out _))
            {
                throw new StateBackupValidationException(
                    "紧急恢复后的 SQLite 文件大小校验失败。");
            }

            EnsurePlainFile(
                specification.SourcePath,
                "紧急恢复后的 SQLite 文件类型无效。");
            if (new FileInfo(specification.SourcePath).Length != file.Size)
            {
                throw new StateBackupValidationException(
                    "紧急恢复后的 SQLite 文件大小校验失败。");
            }

            var actualSha256 = await StateBundleArchive.ComputeSha256Async(
                specification.SourcePath,
                cancellationToken);
            if (!FixedTimeEquals(file.Sha256!, actualSha256))
            {
                throw new StateBackupValidationException(
                    "紧急恢复后的 SQLite 文件校验值不一致。");
            }
        }
    }

    private async Task<RawStateSafetyManifest> ReadAndValidateRawSafetyAsync(
        PendingStateRestorePlan plan,
        CancellationToken cancellationToken)
    {
        var emergencyRoot = Path.Combine(
            _protectionRoot,
            LocalStateProtectionService.EmergencySafetyDirectoryName);
        StateProtectionPathGuard.ValidateExistingDirectory(
            _protectionRoot,
            emergencyRoot);
        var safetyDirectory = LocalStateProtectionService.GetEmergencySafetyDirectory(
            _protectionRoot,
            plan.RestoreId);
        if (!StateProtectionPathGuard.PathsEqual(
                safetyDirectory,
                plan.SafetyBackupPath))
        {
            throw new StateBackupValidationException(
                "紧急恢复安全目录与恢复计划不一致。");
        }

        StateProtectionPathGuard.ValidateExistingDirectory(
            emergencyRoot,
            safetyDirectory);
        var manifestPath = Path.Combine(safetyDirectory, RawSafetyManifestFileName);
        if (!StateProtectionPathGuard.TryGetAttributes(manifestPath, out _) ||
            string.IsNullOrWhiteSpace(plan.RawSafetyManifestSha256))
        {
            throw new StateBackupValidationException("紧急恢复安全清单不存在。");
        }

        EnsurePlainFile(manifestPath, "紧急恢复安全清单不能是符号链接。");

        var info = new FileInfo(manifestPath);
        if (info.Length <= 0 || info.Length > 256 * 1024)
        {
            throw new StateBackupValidationException("紧急恢复安全清单大小无效。");
        }

        var actualManifestSha256 = await StateBundleArchive.ComputeSha256Async(
            manifestPath,
            cancellationToken);
        if (!FixedTimeEquals(
                plan.RawSafetyManifestSha256,
                actualManifestSha256))
        {
            throw new StateBackupValidationException("紧急恢复安全清单校验失败。");
        }

        RawStateSafetyManifest manifest;
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<RawStateSafetyManifest>(
                    stream,
                    StateBundleArchive.JsonOptions,
                    cancellationToken)
                ?? throw new StateBackupValidationException(
                    "紧急恢复安全清单为空。");
        }
        catch (JsonException exception)
        {
            throw new StateBackupValidationException(
                "紧急恢复安全清单格式无效。",
                exception);
        }

        ValidateRawSafetyManifest(manifest, plan.RestoreId);
        foreach (var file in manifest.Files.Where(file => file.Existed))
        {
            var path = Path.Combine(safetyDirectory, file.FileName);
            if (!StateProtectionPathGuard.TryGetAttributes(path, out _))
            {
                throw new StateBackupValidationException(
                    "紧急恢复安全文件不存在或大小不一致。");
            }

            EnsurePlainFile(path, "紧急恢复安全文件不能是符号链接。");
            if (new FileInfo(path).Length != file.Size)
            {
                throw new StateBackupValidationException(
                    "紧急恢复安全文件不存在或大小不一致。");
            }

            var sha256 = await StateBundleArchive.ComputeSha256Async(
                path,
                cancellationToken);
            if (!FixedTimeEquals(file.Sha256!, sha256))
            {
                throw new StateBackupValidationException(
                    "紧急恢复安全文件校验失败。");
            }
        }

        _validatedRawSafetyRestoreIds.Add(plan.RestoreId);
        return manifest;
    }

    private void ValidateRawSafetyManifest(
        RawStateSafetyManifest manifest,
        Guid restoreId)
    {
        var specifications = GetRawSafetyFileSpecifications();
        if (manifest.FormatVersion != StateBundleArchive.CurrentFormatVersion ||
            manifest.RestoreId != restoreId ||
            manifest.CreatedAtUtc == default ||
            manifest.Files is null ||
            manifest.Files.Count != specifications.Count)
        {
            throw new StateBackupValidationException(
                "紧急恢复安全清单内容无效。");
        }

        var files = new Dictionary<string, RawStateSafetyFileManifest>(
            StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (file is null ||
                string.IsNullOrWhiteSpace(file.LogicalName) ||
                !files.TryAdd(file.LogicalName, file))
            {
                throw new StateBackupValidationException(
                    "紧急恢复安全清单包含重复文件。");
            }
        }

        foreach (var specification in specifications)
        {
            if (!files.TryGetValue(specification.LogicalName, out var file) ||
                !string.Equals(
                    file.FileName,
                    specification.StoredFileName,
                    StringComparison.Ordinal) ||
                (file.Existed &&
                 (file.Size < 0 || !IsSha256(file.Sha256))) ||
                (!file.Existed &&
                 (file.Size != 0 || file.Sha256 is not null)))
            {
                throw new StateBackupValidationException(
                    "紧急恢复安全清单的文件描述无效。");
            }
        }
    }

    private static async Task UpgradePreparedDatabasesAsync(
        ExtractedStateBundle bundle,
        CancellationToken cancellationToken)
    {
        var assetConnectionString = CreateWritableConnectionString(
            bundle.AssetDatabasePath);
        await DatabaseMigrator.MigrateAsync(
            assetConnectionString,
            cancellationToken);
        await CollapseWalAsync(bundle.AssetDatabasePath, cancellationToken);

        var readerConnectionString = CreateWritableConnectionString(
            bundle.ReaderDatabasePath);
        await Reader.ReaderDatabaseMigrator.MigrateAsync(
            readerConnectionString,
            cancellationToken);
        await CollapseWalAsync(bundle.ReaderDatabasePath, cancellationToken);
    }

    private static async Task CollapseWalAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            CreateWritableConnectionString(databasePath));
        await connection.OpenAsync(cancellationToken);
        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=DELETE;";
            var mode = Convert.ToString(
                await journalMode.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new StateBackupValidationException(
                    "无法将待恢复数据库转换为独立 SQLite 文件。");
            }
        }
    }

    private static string CreateWritableConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString();

    private void ReplaceDatabase(
        string sourcePath,
        string destinationPath,
        string displacedPath,
        string role,
        bool invokeFaultHook)
    {
        if (invokeFaultHook)
        {
            _beforeReplace?.Invoke(role);
        }

        var destinationExists = StateProtectionPathGuard.TryGetAttributes(
            destinationPath,
            out _);
        if (destinationExists)
        {
            EnsurePlainFile(destinationPath, "SQLite 数据库目标不能是目录或符号链接。");
        }

        EnsureSqliteSidecarsArePlain(destinationPath);
        DeleteSqliteSidecars(destinationPath);
        TryDeleteFile(displacedPath);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("数据库路径没有父目录。");
        Directory.CreateDirectory(directory);
        if (destinationExists)
        {
            File.Replace(
                sourcePath,
                destinationPath,
                displacedPath,
                ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(sourcePath, destinationPath);
        }
    }

    private async Task<PendingStateRestorePlan> ReadAndValidatePlanAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            EnsurePlainFile(_pendingPlanPath, "挂起的状态恢复计划不能是符号链接。");
            var info = new FileInfo(_pendingPlanPath);
            if (info.Length <= 0 || info.Length > 256 * 1024)
            {
                throw new StateBackupValidationException("挂起的状态恢复计划大小无效。");
            }

            await using var stream = new FileStream(
                _pendingPlanPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var plan = await JsonSerializer.DeserializeAsync<PendingStateRestorePlan>(
                    stream,
                    StateBundleArchive.JsonOptions,
                    cancellationToken)
                ?? throw new StateBackupValidationException("挂起的状态恢复计划为空。");
            ValidatePlan(plan);
            return plan;
        }
        catch (StateBackupValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new StateBackupValidationException("挂起的状态恢复计划无效。", exception);
        }
    }

    private static void ValidatePlan(PendingStateRestorePlan plan)
    {
        string[] supportedPhases =
        [
            LocalStateProtectionService.PreparedPhase,
            SafetyCapturedPhase,
            ApplyingPhase,
            AssetAppliedPhase,
            ReaderAppliedPhase,
            VerifyingPhase,
            CompletedPhase,
            RolledBackPhase,
            AbandonedPhase,
            RollbackFailedPhase
        ];
        var bundleSafety = IsBundleSafety(plan) &&
            string.Equals(
                plan.StagedSafetyBundleFileName,
                LocalStateProtectionService.SafetyBundleFileName,
                StringComparison.Ordinal) &&
            IsSha256(plan.SafetyBundleSha256) &&
            plan.RawSafetyManifestSha256 is null;
        var rawSafetyHashIsValid = IsSha256(plan.RawSafetyManifestSha256);
        var rawSafety = IsRawFilesSafety(plan) &&
            plan.StagedSafetyBundleFileName is null &&
            plan.SafetyBundleSha256 is null &&
            (string.Equals(
                    plan.Phase,
                    LocalStateProtectionService.PreparedPhase,
                    StringComparison.Ordinal) ||
                string.Equals(
                    plan.Phase,
                    AbandonedPhase,
                    StringComparison.Ordinal)
                ? plan.RawSafetyManifestSha256 is null || rawSafetyHashIsValid
                : rawSafetyHashIsValid);
        if (plan.FormatVersion != StateBundleArchive.CurrentFormatVersion ||
            plan.RestoreId == Guid.Empty ||
            plan.PreparedAtUtc == default ||
            !string.Equals(
                plan.StagedBundleFileName,
                LocalStateProtectionService.RestoreBundleFileName,
                StringComparison.Ordinal) ||
            !IsValidFullyQualifiedPath(plan.SafetyBackupPath) ||
            !supportedPhases.Contains(plan.Phase, StringComparer.Ordinal) ||
            !IsSha256(plan.BundleSha256) ||
            (!bundleSafety && !rawSafety))
        {
            throw new StateBackupValidationException("挂起的状态恢复计划内容无效。");
        }
    }

    private string GetValidatedOperationDirectory(PendingStateRestorePlan plan)
    {
        var pendingRoot = Path.Combine(
            _protectionRoot,
            LocalStateProtectionService.PendingDirectoryName);
        StateProtectionPathGuard.ValidateExistingDirectory(
            _protectionRoot,
            pendingRoot);
        var directory = LocalStateProtectionService.GetOperationDirectory(
            _protectionRoot,
            plan.RestoreId);
        StateProtectionPathGuard.EnsureContained(
            pendingRoot,
            directory,
            allowRoot: false);
        if (!StateProtectionPathGuard.TryGetAttributes(directory, out _))
        {
            throw new StateBackupValidationException("挂起的状态恢复目录无效。");
        }

        return StateProtectionPathGuard.ValidateExistingDirectory(
            pendingRoot,
            directory);
    }

    private static async Task VerifyStagedFileAsync(
        string operationDirectory,
        string fileName,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(operationDirectory, fileName);
        if (!StateProtectionPathGuard.TryGetAttributes(path, out _))
        {
            throw new StateBackupValidationException("挂起的状态恢复文件不存在。");
        }

        EnsurePlainFile(path, "挂起的状态恢复文件不能是符号链接。");
        EnsureStagedBundleLengthWithinLimit(path);

        var actualSha256 = await StateBundleArchive.ComputeSha256Async(
            path,
            cancellationToken);
        if (!FixedTimeEquals(expectedSha256, actualSha256))
        {
            throw new StateBackupValidationException(
                "挂起的状态恢复文件校验失败，文件可能已损坏或被修改。");
        }
    }

    internal static void EnsureStagedBundleLengthWithinLimit(
        string path,
        long maximumArchiveBytes = StateBundleArchive.MaximumArchiveBytes)
    {
        if (maximumArchiveBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumArchiveBytes));
        }

        var length = new FileInfo(path).Length;
        if (length <= 0 || length > maximumArchiveBytes)
        {
            throw new StateBackupValidationException(
                "挂起的状态恢复文件大小超过安全限制。");
        }
    }

    private Task WritePlanAsync(
        PendingStateRestorePlan plan,
        CancellationToken cancellationToken) =>
        LocalStateProtectionService.WritePendingPlanAsync(
            _pendingPlanPath,
            plan,
            overwrite: true,
            cancellationToken);

    private async Task TryWritePhaseAsync(
        PendingStateRestorePlan plan,
        string phase)
    {
        try
        {
            await WritePlanAsync(
                plan with { Phase = phase },
                CancellationToken.None);
        }
        catch
        {
            // Preserve the staged files even if the failure marker cannot be written.
        }
    }

    private void CompleteCleanup(string operationDirectory, Guid restoreId)
    {
        if (!TryWriteCleanupMarker(restoreId))
        {
            return;
        }

        if (TryDeleteFile(_pendingPlanPath))
        {
            TryCompleteDeferredCleanup();
        }
    }

    private void ResetControlledDirectory(string path)
    {
        StateProtectionPathGuard.ResetDirectory(_protectionRoot, path);
    }

    private void CleanupDisposableExtractionDirectories(
        string operationDirectory,
        bool preserveSafety)
    {
        var directoryNames = preserveSafety
            ? new[] { "incoming", "rollback" }
            : new[] { "incoming", "safety", "rollback" };
        foreach (var name in directoryNames)
        {
            var path = Path.Combine(operationDirectory, name);
            StateProtectionPathGuard.EnsureContained(
                operationDirectory,
                path,
                allowRoot: false);
            if (!StateProtectionPathGuard.TryGetAttributes(path, out _))
            {
                continue;
            }

            if (!StateProtectionPathGuard.TryDeleteDirectory(_protectionRoot, path))
            {
                throw new IOException(
                    $"无法清理上次恢复留下的暂存目录：{path}");
            }
        }
    }

    private static void DeleteSqliteSidecars(string databasePath)
    {
        File.Delete($"{databasePath}-wal");
        File.Delete($"{databasePath}-shm");
        File.Delete($"{databasePath}-journal");
    }

    private static bool IsSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
        {
            return false;
        }

        try
        {
            return Convert.FromHexString(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string expectedHex, string actualHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHex),
                Convert.FromHexString(actualHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsValidFullyQualifiedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }

            _ = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          NotSupportedException or
                                          PathTooLongException)
        {
            return false;
        }
    }

    private string? GetReportableSafetyPath(PendingStateRestorePlan? plan)
    {
        if (plan is null || !IsValidFullyQualifiedPath(plan.SafetyBackupPath))
        {
            return null;
        }

        var path = Path.GetFullPath(plan.SafetyBackupPath);
        if (!IsRawFilesSafety(plan))
        {
            return path;
        }

        var expectedPath = LocalStateProtectionService.GetEmergencySafetyDirectory(
            _protectionRoot,
            plan.RestoreId);
        var manifestPath = Path.Combine(expectedPath, RawSafetyManifestFileName);
        if (!StateProtectionPathGuard.PathsEqual(path, expectedPath) ||
            !IsSha256(plan.RawSafetyManifestSha256) ||
            !_validatedRawSafetyRestoreIds.Contains(plan.RestoreId) ||
            !StateProtectionPathGuard.TryGetAttributes(expectedPath, out _) ||
            !StateProtectionPathGuard.TryGetAttributes(manifestPath, out _))
        {
            return null;
        }

        try
        {
            StateProtectionPathGuard.ValidateExistingDirectory(
                Path.Combine(
                    _protectionRoot,
                    LocalStateProtectionService.EmergencySafetyDirectoryName),
                expectedPath);
            EnsurePlainFile(manifestPath, "紧急恢复安全清单不能是符号链接。");
            return path;
        }
        catch (Exception exception) when (exception is StateBackupValidationException or
                                          IOException or
                                          UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return !StateProtectionPathGuard.TryGetAttributes(path, out _);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void EnsureProtectionRootForStartup()
    {
        Directory.CreateDirectory(_dataDirectory);
        _ = StateProtectionPathGuard.ValidateExistingDirectory(
            _dataDirectory,
            _dataDirectory);
        _ = StateProtectionPathGuard.EnsureDirectory(
            _dataDirectory,
            _protectionRoot);
    }

    private void ValidateKnownProtectionDirectories()
    {
        foreach (var name in new[]
                 {
                     "Temp",
                     LocalStateProtectionService.PendingDirectoryName,
                     LocalStateProtectionService.EmergencySafetyDirectoryName
                 })
        {
            var path = Path.Combine(_protectionRoot, name);
            if (StateProtectionPathGuard.TryGetAttributes(
                    path,
                    out var attributes) &&
                (attributes & FileAttributes.Directory) == 0)
            {
                throw new StateBackupValidationException(
                    $"状态保护目录被文件占用：{path}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                StateProtectionPathGuard.ValidateExistingDirectory(
                    _protectionRoot,
                    path);
            }
        }
    }

    private void TryCleanupOrphanedWorkDirectories()
    {
        TryCleanupChildren(Path.Combine(_protectionRoot, "Temp"));
        TryCleanupChildren(Path.Combine(
            _protectionRoot,
            LocalStateProtectionService.PendingDirectoryName));
    }

    private void CleanupOwnedStartupTemporaryFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         _protectionRoot,
                         "*.tmp",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (!IsAtomicJsonTemporaryFileName(
                        name,
                        $".{LocalStateProtectionService.PendingPlanFileName}.") &&
                    !IsAtomicJsonTemporaryFileName(
                        name,
                        $"{LocalStateProtectionService.PendingCleanupMarkerFileName}."))
                {
                    continue;
                }

                DeleteOwnedStartupTemporaryFile(_protectionRoot, path);
            }

            foreach (var specification in GetRawSafetyFileSpecifications())
            {
                var directory = Path.GetDirectoryName(specification.SourcePath)
                    ?? throw new StateBackupValidationException(
                        "SQLite 文件路径没有父目录。");
                if (!StateProtectionPathGuard.TryGetAttributes(directory, out _))
                {
                    continue;
                }

                var prefix = $".{Path.GetFileName(specification.SourcePath)}.";
                foreach (var path in Directory.EnumerateFileSystemEntries(
                             directory,
                             $"{prefix}*.restore.tmp",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!IsRawRestoreTemporaryFileName(
                            Path.GetFileName(path),
                            prefix))
                    {
                        continue;
                    }

                    DeleteOwnedStartupTemporaryFile(directory, path);
                }
            }
        }
        catch (StateBackupValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException)
        {
            throw new StateBackupValidationException(
                "无法安全清理上次启动遗留的状态恢复临时文件。",
                exception);
        }
    }

    private static void DeleteOwnedStartupTemporaryFile(
        string controlledDirectory,
        string path)
    {
        StateProtectionPathGuard.EnsureContained(
            controlledDirectory,
            path,
            allowRoot: false);
        EnsurePlainFile(path, "状态恢复临时文件不能是目录或符号链接。");
        if (!TryDeleteFile(path))
        {
            throw new StateBackupValidationException(
                $"无法清理状态恢复临时文件：{path}");
        }
    }

    private static bool IsAtomicJsonTemporaryFileName(
        string name,
        string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        var guid = name.Substring(
            prefix.Length,
            name.Length - prefix.Length - ".tmp".Length);
        return Guid.TryParseExact(guid, "N", out _);
    }

    private static bool IsRawRestoreTemporaryFileName(
        string name,
        string prefix)
    {
        const string suffix = ".restore.tmp";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var guid = name.Substring(
            prefix.Length,
            name.Length - prefix.Length - suffix.Length);
        return Guid.TryParseExact(guid, "N", out _);
    }

    private void TryCleanupChildren(string root)
    {
        if (!StateProtectionPathGuard.TryGetAttributes(root, out _))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            _ = StateProtectionPathGuard.TryDeleteDirectory(
                _protectionRoot,
                directory);
        }

        foreach (var file in Directory.EnumerateFiles(root))
        {
            TryDeleteFile(file);
        }
    }

    private bool TryWriteCleanupMarker(Guid restoreId)
    {
        var markerPath = Path.Combine(
            _protectionRoot,
            LocalStateProtectionService.PendingCleanupMarkerFileName);
        var temporaryPath = $"{markerPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    new PendingCleanupMarker(
                        StateBundleArchive.CurrentFormatVersion,
                        restoreId),
                    StateBundleArchive.JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, markerPath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private void TryCompleteDeferredCleanup()
    {
        var markerPath = Path.Combine(
            _protectionRoot,
            LocalStateProtectionService.PendingCleanupMarkerFileName);
        if (!StateProtectionPathGuard.TryGetAttributes(markerPath, out _))
        {
            return;
        }

        EnsurePlainFile(markerPath, "状态恢复清理记录不能是符号链接。");
        if (StateProtectionPathGuard.TryGetAttributes(_pendingPlanPath, out _))
        {
            return;
        }

        PendingCleanupMarker? marker;
        try
        {
            var info = new FileInfo(markerPath);
            if (info.Length <= 0 || info.Length > 16 * 1024)
            {
                TryDeleteFile(markerPath);
                return;
            }

            using var stream = new FileStream(
                markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.SequentialScan);
            marker = JsonSerializer.Deserialize<PendingCleanupMarker>(
                stream,
                StateBundleArchive.JsonOptions);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (JsonException)
        {
            TryDeleteFile(markerPath);
            return;
        }

        if (marker is null ||
            marker.FormatVersion != StateBundleArchive.CurrentFormatVersion ||
            marker.RestoreId == Guid.Empty)
        {
            TryDeleteFile(markerPath);
            return;
        }

        var operationDirectory = LocalStateProtectionService.GetOperationDirectory(
            _protectionRoot,
            marker.RestoreId);
        if (StateProtectionPathGuard.TryGetAttributes(operationDirectory, out _) &&
            !StateProtectionPathGuard.TryDeleteDirectory(
                _protectionRoot,
                operationDirectory))
        {
            return;
        }

        TryDeleteFile(markerPath);
    }

    private static bool IsBundleSafety(PendingStateRestorePlan plan) =>
        string.Equals(
            plan.SafetyKind,
            LocalStateProtectionService.BundleSafetyKind,
            StringComparison.Ordinal);

    private static bool IsRawFilesSafety(PendingStateRestorePlan plan) =>
        string.Equals(
            plan.SafetyKind,
            LocalStateProtectionService.RawFilesSafetyKind,
            StringComparison.Ordinal);

    private static bool IsCleanupOnlyPhase(string phase) =>
        string.Equals(phase, CompletedPhase, StringComparison.Ordinal) ||
        string.Equals(phase, RolledBackPhase, StringComparison.Ordinal) ||
        string.Equals(phase, AbandonedPhase, StringComparison.Ordinal);

    private static void EnsurePlainFile(string path, string message)
    {
        if (!StateProtectionPathGuard.TryGetAttributes(path, out var attributes))
        {
            throw new StateBackupValidationException($"{message} 文件不存在。");
        }

        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new StateBackupValidationException(message);
        }
    }

    private static void EnsureSqliteSidecarsArePlain(string databasePath)
    {
        foreach (var path in new[]
                 {
                     $"{databasePath}-wal",
                     $"{databasePath}-shm",
                     $"{databasePath}-journal"
                 })
        {
            if (StateProtectionPathGuard.TryGetAttributes(path, out _))
            {
                EnsurePlainFile(path, "SQLite sidecar 不能是目录或符号链接。");
            }
        }
    }

    private IReadOnlyList<RawSafetyFileSpecification> GetRawSafetyFileSpecifications() =>
    [
        new("asset", "cdsi.db", _assetDatabasePath),
        new("asset-wal", "cdsi.db-wal", $"{_assetDatabasePath}-wal"),
        new("asset-shm", "cdsi.db-shm", $"{_assetDatabasePath}-shm"),
        new("asset-journal", "cdsi.db-journal", $"{_assetDatabasePath}-journal"),
        new("reader", "reader.db", _readerDatabasePath),
        new("reader-wal", "reader.db-wal", $"{_readerDatabasePath}-wal"),
        new("reader-shm", "reader.db-shm", $"{_readerDatabasePath}-shm"),
        new("reader-journal", "reader.db-journal", $"{_readerDatabasePath}-journal")
    ];

    private static async Task CopyRawFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
    }

    private static async Task RestoreRawFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("SQLite 文件没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.restore.tmp");
        try
        {
            await CopyRawFileAsync(sourcePath, temporaryPath, cancellationToken);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task WriteJsonFileAtomicallyAsync<T>(
        string path,
        T value,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("状态保护文件没有父目录。");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    StateBundleArchive.JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private sealed record RawSafetyFileSpecification(
        string LogicalName,
        string StoredFileName,
        string SourcePath);

    private sealed record PendingCleanupMarker(int FormatVersion, Guid RestoreId);
}
