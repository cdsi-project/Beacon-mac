using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed class LocalStateProtectionService
{
    internal const string ProtectionDirectoryName = "StateProtection";
    internal const string PendingDirectoryName = "Pending";
    internal const string EmergencySafetyDirectoryName = "EmergencySafety";
    internal const string PendingPlanFileName = "pending-restore.json";
    internal const string PendingCleanupMarkerFileName = "pending-cleanup.json";
    internal const string RestoreBundleFileName = "restore.cdsibak";
    internal const string SafetyBundleFileName = "safety.cdsibak";
    internal const string PreparedPhase = "Prepared";
    internal const string BundleSafetyKind = "Bundle";
    internal const string RawFilesSafetyKind = "RawFiles";

    private const string BackupTimestampFormat = "yyyyMMdd-HHmmss-fff'Z'";
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(1);

    private readonly string _dataDirectory;
    private readonly string _applicationVersion;
    private readonly LocalDatabaseBackupService _assetSnapshotService;
    private readonly LocalDatabaseBackupService _readerSnapshotService;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public LocalStateProtectionService(
        string dataDirectory,
        string assetDatabasePath,
        string readerDatabasePath,
        string applicationVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(readerDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);

        _dataDirectory = Path.GetFullPath(dataDirectory);
        _applicationVersion = applicationVersion.Trim();
        _assetSnapshotService = new LocalDatabaseBackupService(
            assetDatabasePath,
            _applicationVersion);
        _readerSnapshotService = new LocalDatabaseBackupService(
            readerDatabasePath,
            _applicationVersion,
            "Reader");
    }

    public string GetBackupDirectory(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return Path.Combine(
            Path.GetFullPath(workspacePath),
            "System",
            "StateBackups");
    }

    public async Task<IReadOnlyList<LocalStateBackupInfo>> ListBackupsAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var backupDirectory = GetBackupDirectory(workspacePath);
            ValidateWorkspaceBackupPath(workspacePath, backupDirectory);
            if (!StateProtectionPathGuard.TryGetAttributes(
                    backupDirectory,
                    out var backupDirectoryAttributes))
            {
                return [];
            }

            if ((backupDirectoryAttributes & FileAttributes.Directory) == 0 ||
                (backupDirectoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "状态备份目录不是安全的普通目录。");
            }

            CleanupStaleBackupTemporaryFiles(backupDirectory);

            var backups = new List<LocalStateBackupInfo>();
            foreach (var path in Directory
                         .EnumerateFiles(
                             backupDirectory,
                             "*.cdsibak",
                             SearchOption.TopDirectoryOnly)
                         .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                backups.Add(await InspectCoreAsync(path, cancellationToken));
            }

            return backups
                .OrderByDescending(backup => backup.CreatedAtUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(
                    backup => Path.GetFileName(backup.Path),
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<LocalStateBackupInfo> CreateBackupAsync(
        string workspacePath,
        LocalStateBackupKind kind,
        string sourceClientId,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceClientId(sourceClientId);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var backupDirectory = GetBackupDirectory(workspacePath);
            EnsureWorkspaceDirectory(workspacePath, backupDirectory);
            CleanupStaleBackupTemporaryFiles(backupDirectory);
            var backupId = Guid.NewGuid();
            var createdAtUtc = DateTimeOffset.UtcNow;
            var destinationPath = Path.Combine(
                backupDirectory,
                CreateBackupFileName(kind, createdAtUtc, backupId));
            return await CreateBundleCoreAsync(
                destinationPath,
                kind,
                sourceClientId,
                backupId,
                createdAtUtc,
                overwrite: false,
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<LocalStateBackupInfo> InspectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await InspectCoreAsync(path, cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<string> ExportAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        ExportCoreAsync(
            sourcePath,
            destinationPath,
            expectedBackup: null,
            overwrite,
            cancellationToken);

    public Task<string> ExportAsync(
        string sourcePath,
        string destinationPath,
        LocalStateBackupInfo expectedBackup,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedBackup);
        return ExportCoreAsync(
            sourcePath,
            destinationPath,
            expectedBackup,
            overwrite,
            cancellationToken);
    }

    private async Task<string> ExportCoreAsync(
        string sourcePath,
        string destinationPath,
        LocalStateBackupInfo? expectedBackup,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var source = Path.GetFullPath(sourcePath);
            var destination = Path.GetFullPath(destinationPath);
            EnsureBundleExtension(destination);
            var info = expectedBackup ?? await InspectCoreAsync(source, cancellationToken);
            EnsureRestorableExpectedBackup(info);
            if (PathsEqual(source, destination))
            {
                var current = await InspectCoreAsync(source, cancellationToken);
                EnsureRestorable(current);
                EnsureSameBundle(info, current);
                return destination;
            }

            _ = await CopyFileAtomicallyAsync(
                source,
                destination,
                overwrite,
                cancellationToken,
                info.BundleSha256,
                async (temporaryPath, token) =>
                {
                    var copied = await InspectCoreAsync(temporaryPath, token);
                    EnsureRestorable(copied);
                    EnsureSameBundle(info, copied);
                });
            return destination;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<StateRestorePreparation> PrepareRestoreAsync(
        string path,
        string workspacePath,
        string sourceClientId,
        CancellationToken cancellationToken = default) =>
        PrepareRestoreCoreAsync(
            path,
            workspacePath,
            sourceClientId,
            expectedBackup: null,
            cancellationToken);

    public Task<StateRestorePreparation> PrepareRestoreAsync(
        string path,
        string workspacePath,
        string sourceClientId,
        LocalStateBackupInfo expectedBackup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedBackup);
        return PrepareRestoreCoreAsync(
            path,
            workspacePath,
            sourceClientId,
            expectedBackup,
            cancellationToken);
    }

    private async Task<StateRestorePreparation> PrepareRestoreCoreAsync(
        string path,
        string workspacePath,
        string sourceClientId,
        LocalStateBackupInfo? expectedBackup,
        CancellationToken cancellationToken)
    {
        ValidateSourceClientId(sourceClientId);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var protectionRoot = EnsureProtectionRoot();
            var pendingPlanPath = Path.Combine(
                protectionRoot,
                PendingPlanFileName);
            EnsureNoPendingRestore(protectionRoot, pendingPlanPath);

            if (expectedBackup is null)
            {
                expectedBackup = await InspectCoreAsync(path, cancellationToken);
            }

            EnsureRestorableExpectedBackup(expectedBackup);

            var restoreId = Guid.NewGuid();
            var pendingRoot = StateProtectionPathGuard.EnsureDirectory(
                protectionRoot,
                Path.Combine(protectionRoot, PendingDirectoryName));
            var operationDirectory = StateProtectionPathGuard.EnsureDirectory(
                pendingRoot,
                GetOperationDirectory(protectionRoot, restoreId));
            var safetyExportPath = string.Empty;
            try
            {
                var stagedBundlePath = Path.Combine(
                    operationDirectory,
                    RestoreBundleFileName);
                var copy = await CopyFileAtomicallyAsync(
                    path,
                    stagedBundlePath,
                    overwrite: false,
                    cancellationToken,
                    expectedBackup.BundleSha256);
                var stagedInfo = await InspectCoreAsync(
                    stagedBundlePath,
                    cancellationToken);
                EnsureRestorable(stagedInfo);
                EnsureSameBundle(expectedBackup, stagedInfo);
                EnsureBundleHash(copy.Sha256, stagedInfo.BundleSha256);
                var sourceInfo = stagedInfo with { Path = Path.GetFullPath(path) };

                var safetyBundlePath = Path.Combine(
                    operationDirectory,
                    SafetyBundleFileName);
                var safetyBackupId = Guid.NewGuid();
                var safetyCreatedAtUtc = DateTimeOffset.UtcNow;
                _ = await CreateBundleCoreAsync(
                    safetyBundlePath,
                    LocalStateBackupKind.PreRestore,
                    sourceClientId,
                    safetyBackupId,
                    safetyCreatedAtUtc,
                    overwrite: false,
                    cancellationToken);
                var safetySha256 = await StateBundleArchive.ComputeSha256Async(
                    safetyBundlePath,
                    cancellationToken);

                var backupDirectory = GetBackupDirectory(workspacePath);
                EnsureWorkspaceDirectory(workspacePath, backupDirectory);
                CleanupStaleBackupTemporaryFiles(backupDirectory);
                safetyExportPath = Path.Combine(
                    backupDirectory,
                    CreateBackupFileName(
                        LocalStateBackupKind.PreRestore,
                        safetyCreatedAtUtc,
                        safetyBackupId));
                _ = await CopyFileAtomicallyAsync(
                    safetyBundlePath,
                    safetyExportPath,
                    overwrite: false,
                    cancellationToken,
                    safetySha256);

                var plan = new PendingStateRestorePlan(
                    StateBundleArchive.CurrentFormatVersion,
                    restoreId,
                    DateTimeOffset.UtcNow,
                    RestoreBundleFileName,
                    copy.Sha256,
                    BundleSafetyKind,
                    SafetyBundleFileName,
                    safetySha256,
                    RawSafetyManifestSha256: null,
                    safetyExportPath,
                    PreparedPhase);
                await WritePendingPlanAsync(
                    pendingPlanPath,
                    plan,
                    overwrite: false,
                    cancellationToken);
                return new StateRestorePreparation(
                    restoreId,
                    sourceInfo,
                    safetyExportPath);
            }
            catch
            {
                _ = StateProtectionPathGuard.TryDeleteDirectory(
                    protectionRoot,
                    operationDirectory);
                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task<StateRestorePreparation> PrepareEmergencyRestoreAsync(
        string path,
        string sourceClientId,
        CancellationToken cancellationToken = default) =>
        PrepareEmergencyRestoreCoreAsync(
            path,
            sourceClientId,
            expectedBackup: null,
            cancellationToken);

    public Task<StateRestorePreparation> PrepareEmergencyRestoreAsync(
        string path,
        string sourceClientId,
        LocalStateBackupInfo expectedBackup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedBackup);
        return PrepareEmergencyRestoreCoreAsync(
            path,
            sourceClientId,
            expectedBackup,
            cancellationToken);
    }

    private async Task<StateRestorePreparation> PrepareEmergencyRestoreCoreAsync(
        string path,
        string sourceClientId,
        LocalStateBackupInfo? expectedBackup,
        CancellationToken cancellationToken)
    {
        ValidateSourceClientId(sourceClientId);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var protectionRoot = EnsureProtectionRoot();
            var pendingPlanPath = Path.Combine(
                protectionRoot,
                PendingPlanFileName);
            EnsureNoPendingRestore(protectionRoot, pendingPlanPath);

            if (expectedBackup is null)
            {
                expectedBackup = await InspectCoreAsync(path, cancellationToken);
            }

            EnsureRestorableExpectedBackup(expectedBackup);

            var restoreId = Guid.NewGuid();
            var pendingRoot = StateProtectionPathGuard.EnsureDirectory(
                protectionRoot,
                Path.Combine(protectionRoot, PendingDirectoryName));
            var operationDirectory = StateProtectionPathGuard.EnsureDirectory(
                pendingRoot,
                GetOperationDirectory(protectionRoot, restoreId));
            try
            {
                var stagedBundlePath = Path.Combine(
                    operationDirectory,
                    RestoreBundleFileName);
                var copy = await CopyFileAtomicallyAsync(
                    path,
                    stagedBundlePath,
                    overwrite: false,
                    cancellationToken,
                    expectedBackup.BundleSha256);
                var stagedInfo = await InspectCoreAsync(
                    stagedBundlePath,
                    cancellationToken);
                EnsureRestorable(stagedInfo);
                EnsureSameBundle(expectedBackup, stagedInfo);
                EnsureBundleHash(copy.Sha256, stagedInfo.BundleSha256);
                var sourceInfo = stagedInfo with { Path = Path.GetFullPath(path) };
                var emergencySafetyPath = GetEmergencySafetyDirectory(
                    protectionRoot,
                    restoreId);
                var plan = new PendingStateRestorePlan(
                    StateBundleArchive.CurrentFormatVersion,
                    restoreId,
                    DateTimeOffset.UtcNow,
                    RestoreBundleFileName,
                    copy.Sha256,
                    RawFilesSafetyKind,
                    StagedSafetyBundleFileName: null,
                    SafetyBundleSha256: null,
                    RawSafetyManifestSha256: null,
                    emergencySafetyPath,
                    PreparedPhase);
                await WritePendingPlanAsync(
                    pendingPlanPath,
                    plan,
                    overwrite: false,
                    cancellationToken);
                return new StateRestorePreparation(
                    restoreId,
                    sourceInfo,
                    emergencySafetyPath);
            }
            catch
            {
                _ = StateProtectionPathGuard.TryDeleteDirectory(
                    protectionRoot,
                    operationDirectory);
                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    internal static string GetProtectionRoot(string dataDirectory) =>
        Path.Combine(
            Path.GetFullPath(dataDirectory),
            ProtectionDirectoryName);

    internal static string GetOperationDirectory(
        string protectionRoot,
        Guid restoreId) =>
        Path.Combine(
            protectionRoot,
            PendingDirectoryName,
            restoreId.ToString("N"));

    internal static string GetEmergencySafetyDirectory(
        string protectionRoot,
        Guid restoreId) =>
        Path.Combine(
            protectionRoot,
            EmergencySafetyDirectoryName,
            restoreId.ToString("N"));

    internal static async Task WritePendingPlanAsync(
        string path,
        PendingStateRestorePlan plan,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("恢复计划路径没有父目录。");
        Directory.CreateDirectory(directory);
        if (!overwrite && File.Exists(fullPath))
        {
            throw new IOException("已有等待应用的状态恢复。");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
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
                await System.Text.Json.JsonSerializer.SerializeAsync(
                    stream,
                    plan,
                    StateBundleArchive.JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task<LocalStateBackupInfo> CreateBundleCoreAsync(
        string destinationPath,
        LocalStateBackupKind kind,
        string sourceClientId,
        Guid backupId,
        DateTimeOffset createdAtUtc,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationPath);
        EnsureBundleExtension(destination);
        var workDirectory = CreateWorkDirectory("create");
        var published = false;
        try
        {
            var assetSnapshotPath = Path.Combine(workDirectory, "cdsi.db");
            var readerSnapshotPath = Path.Combine(workDirectory, "reader.db");
            await _assetSnapshotService.CreateVerifiedSnapshotFileAsync(
                assetSnapshotPath,
                cancellationToken);
            await _readerSnapshotService.CreateVerifiedSnapshotFileAsync(
                readerSnapshotPath,
                cancellationToken);

            var assetManifest = await StateBundleArchive.CreateDatabaseManifestAsync(
                "asset",
                StateBundleArchive.AssetDatabaseEntryName,
                assetSnapshotPath,
                cancellationToken);
            var readerManifest = await StateBundleArchive.CreateDatabaseManifestAsync(
                "reader",
                StateBundleArchive.ReaderDatabaseEntryName,
                readerSnapshotPath,
                cancellationToken);
            var manifest = new StateBundleManifest(
                StateBundleArchive.FormatName,
                StateBundleArchive.CurrentFormatVersion,
                backupId,
                createdAtUtc,
                _applicationVersion,
                sourceClientId.Trim(),
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Encrypted: false,
                kind.ToString(),
                [assetManifest, readerManifest]);
            await StateBundleArchive.CreateAsync(
                destination,
                manifest,
                assetSnapshotPath,
                readerSnapshotPath,
                overwrite,
                cancellationToken);
            published = true;

            var info = await InspectCoreAsync(destination, cancellationToken);
            EnsureRestorable(info);
            return info;
        }
        catch
        {
            if (published)
            {
                TryDeleteFile(destination);
            }

            throw;
        }
        finally
        {
            var protectionRoot = GetProtectionRoot(_dataDirectory);
            _ = StateProtectionPathGuard.TryDeleteDirectory(
                protectionRoot,
                workDirectory);
        }
    }

    private async Task<LocalStateBackupInfo> InspectCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        long fileSize = 0;
        string? bundleSha256 = null;
        string? workDirectory = null;
        try
        {
            workDirectory = CreateWorkDirectory("inspect");
            var inspectionBundlePath = Path.Combine(
                workDirectory,
                "inspection.cdsibak");
            var copy = await CopyBundleFileAsync(
                fullPath,
                inspectionBundlePath,
                cancellationToken);
            fileSize = copy.Length;
            bundleSha256 = copy.Sha256;
            var extracted = await StateBundleArchive.ExtractAndValidateAsync(
                inspectionBundlePath,
                Path.Combine(workDirectory, "extract"),
                cancellationToken);
            _ = Enum.TryParse<LocalStateBackupKind>(
                extracted.Manifest.BackupKind,
                ignoreCase: true,
                out var kind);
            return new LocalStateBackupInfo(
                fullPath,
                extracted.Manifest.BackupId,
                extracted.Manifest.CreatedAtUtc,
                extracted.Manifest.BeaconVersion,
                kind,
                fileSize,
                LocalStateBackupStatus.Restorable,
                Error: null,
                bundleSha256);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (StateBackupNewerVersionException exception)
        {
            return new LocalStateBackupInfo(
                fullPath,
                null,
                null,
                null,
                null,
                fileSize,
                LocalStateBackupStatus.NewerVersion,
                exception.Message,
                bundleSha256);
        }
        catch (Exception exception) when (
            exception is StateBackupValidationException or
                InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException)
        {
            return new LocalStateBackupInfo(
                fullPath,
                null,
                null,
                null,
                null,
                fileSize,
                LocalStateBackupStatus.Invalid,
                exception.Message,
                bundleSha256);
        }
        finally
        {
            if (workDirectory is not null)
            {
                var protectionRoot = GetProtectionRoot(_dataDirectory);
                _ = StateProtectionPathGuard.TryDeleteDirectory(
                    protectionRoot,
                    workDirectory);
            }
        }
    }

    private string EnsureProtectionRoot()
    {
        var root = GetProtectionRoot(_dataDirectory);
        Directory.CreateDirectory(_dataDirectory);
        _ = StateProtectionPathGuard.ValidateExistingDirectory(
            _dataDirectory,
            _dataDirectory);
        return StateProtectionPathGuard.EnsureDirectory(_dataDirectory, root);
    }

    private string CreateWorkDirectory(string operation)
    {
        var root = EnsureProtectionRoot();
        var temporaryRoot = StateProtectionPathGuard.EnsureDirectory(
            root,
            Path.Combine(root, "Temp"));
        return StateProtectionPathGuard.EnsureDirectory(
            temporaryRoot,
            Path.Combine(
                temporaryRoot,
                $"{operation}-{Guid.NewGuid():N}"));
    }

    private static void EnsureWorkspaceDirectory(
        string workspacePath,
        string destinationPath)
    {
        ValidateWorkspaceBackupPath(workspacePath, destinationPath);
        Directory.CreateDirectory(Path.GetFullPath(destinationPath));
        ValidateWorkspaceBackupPath(workspacePath, destinationPath);
    }

    internal static void ValidateWorkspaceBackupPath(
        string workspacePath,
        string destinationPath)
    {
        var workspace = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(workspacePath));
        var destination = Path.GetFullPath(destinationPath);
        var relative = Path.GetRelativePath(workspace, destination);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("状态备份目录必须位于工作目录内。");
        }

        var current = destination;
        while (true)
        {
            if (StateProtectionPathGuard.TryGetAttributes(current, out var attributes))
            {
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new InvalidOperationException(
                        "工作目录及状态备份路径不能被文件占用。");
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "工作目录及状态备份目录不能包含符号链接或 junction。");
                }
            }

            if (PathsEqual(current, workspace))
            {
                break;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException(
                    "状态备份目录不在有效的工作目录内。");
        }
    }

    private static async Task<BundleCopyResult> CopyFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken,
        string? expectedSha256 = null,
        Func<string, CancellationToken, Task>? validateTemporaryAsync = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("状态备份路径没有父目录。");
        Directory.CreateDirectory(directory);
        if (!overwrite && File.Exists(destination))
        {
            throw new IOException($"状态备份已存在：{destination}");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var copy = await CopyBundleFileAsync(
                source,
                temporaryPath,
                cancellationToken);
            if (expectedSha256 is not null)
            {
                EnsureBundleHash(expectedSha256, copy.Sha256);
            }

            if (validateTemporaryAsync is not null)
            {
                await validateTemporaryAsync(temporaryPath, cancellationToken);
            }

            File.Move(temporaryPath, destination, overwrite);
            return copy;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task<BundleCopyResult> CopyBundleFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var expectedLength = input.Length;
        if (expectedLength <= 0 || expectedLength > StateBundleArchive.MaximumArchiveBytes)
        {
            throw new StateBackupValidationException(
                "状态备份文件大小超过安全限制。");
        }

        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var remainingWithSentinel = checked(expectedLength - total + 1);
                var requested = (int)Math.Min(buffer.Length, remainingWithSentinel);
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (read > expectedLength - total ||
                    read > StateBundleArchive.MaximumArchiveBytes - total)
                {
                    throw new StateBackupValidationException(
                        "状态备份文件在读取期间发生变化或超过安全限制。");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                total += read;
            }

            if (total != expectedLength)
            {
                throw new StateBackupValidationException(
                    "状态备份文件在读取期间发生变化。");
            }

            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            return new BundleCopyResult(
                total,
                Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureNoPendingRestore(
        string protectionRoot,
        string pendingPlanPath)
    {
        var cleanupMarkerPath = Path.Combine(
            protectionRoot,
            PendingCleanupMarkerFileName);
        if (StateProtectionPathGuard.TryGetAttributes(pendingPlanPath, out _) ||
            StateProtectionPathGuard.TryGetAttributes(cleanupMarkerPath, out _))
        {
            throw new InvalidOperationException(
                "已有等待应用的状态恢复，请先重新启动 Beacon。 ");
        }
    }

    private static void EnsureRestorableExpectedBackup(LocalStateBackupInfo backup)
    {
        EnsureRestorable(backup);
        if (backup.BackupId is null || backup.BackupId == Guid.Empty ||
            !IsSha256(backup.BundleSha256))
        {
            throw new StateBackupValidationException(
                "状态备份缺少可用于防止并发替换的身份校验信息，请重新验证备份。");
        }
    }

    private static void EnsureSameBundle(
        LocalStateBackupInfo expected,
        LocalStateBackupInfo actual)
    {
        if (expected.BackupId != actual.BackupId)
        {
            throw new StateBackupValidationException(
                "状态备份已在确认后被替换，恢复已取消。");
        }

        EnsureBundleHash(expected.BundleSha256!, actual.BundleSha256);
    }

    private static void EnsureBundleHash(string expected, string? actual)
    {
        if (!IsSha256(expected) ||
            !IsSha256(actual) ||
            !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new StateBackupValidationException(
                "状态备份已在验证后发生变化，操作已取消。");
        }
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        try
        {
            _ = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void CleanupStaleBackupTemporaryFiles(string backupDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(
                     backupDirectory,
                     ".*.cdsibak.*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            if (!IsOwnedBackupTemporaryFileName(fileName))
            {
                continue;
            }

            TryDeleteStaleTemporaryFile(path);
        }
    }

    private static bool IsOwnedBackupTemporaryFileName(string fileName)
    {
        if (fileName.Length == 0 || fileName[0] != '.' ||
            !fileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        var destinationLength = fileName.Length - 1 - 1 - 32 - 4;
        if (destinationLength <= 0)
        {
            return false;
        }

        var destinationFileName = fileName.Substring(1, destinationLength);
        return (destinationFileName.StartsWith(
                    "beacon-state-",
                    StringComparison.Ordinal) ||
                destinationFileName.StartsWith(
                    "pre-restore-",
                    StringComparison.Ordinal)) &&
            destinationFileName.EndsWith(".cdsibak", StringComparison.OrdinalIgnoreCase) &&
            IsTemporaryFileForDestination(fileName, destinationFileName);
    }

    private static bool IsTemporaryFileForDestination(
        string temporaryFileName,
        string destinationFileName)
    {
        var prefix = $".{destinationFileName}.";
        if (!temporaryFileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !temporaryFileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        var guidText = temporaryFileName.Substring(
            prefix.Length,
            temporaryFileName.Length - prefix.Length - ".tmp".Length);
        return Guid.TryParseExact(guidText, "N", out _);
    }

    private static void TryDeleteStaleTemporaryFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - StaleTemporaryFileAge)
            {
                return;
            }

            TryDeleteFile(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string CreateBackupFileName(
        LocalStateBackupKind kind,
        DateTimeOffset createdAtUtc,
        Guid backupId)
    {
        var prefix = kind == LocalStateBackupKind.PreRestore
            ? "pre-restore"
            : "beacon-state";
        var timestamp = createdAtUtc.UtcDateTime.ToString(
            BackupTimestampFormat,
            CultureInfo.InvariantCulture);
        return $"{prefix}-{timestamp}-{backupId:N}.cdsibak";
    }

    private static void EnsureRestorable(LocalStateBackupInfo info)
    {
        if (info.Status == LocalStateBackupStatus.Restorable)
        {
            return;
        }

        if (info.Status == LocalStateBackupStatus.NewerVersion)
        {
            throw new StateBackupNewerVersionException(
                info.Error ?? "此状态备份需要更高版本的 Beacon。");
        }

        throw new StateBackupValidationException(
            info.Error ?? "状态备份无效，无法恢复。");
    }

    private static void EnsureBundleExtension(string path)
    {
        if (!string.Equals(
                Path.GetExtension(path),
                ".cdsibak",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("状态备份文件必须使用 .cdsibak 扩展名。", nameof(path));
        }
    }

    private static void ValidateSourceClientId(string sourceClientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceClientId);
        if (!Guid.TryParse(sourceClientId.Trim(), out var clientId) ||
            clientId == Guid.Empty)
        {
            throw new ArgumentException("Beacon 客户端 ID 无效。", nameof(sourceClientId));
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record BundleCopyResult(long Length, string Sha256);
}
