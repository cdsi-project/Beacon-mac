using System.Security.Cryptography;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Application.Storage;

public sealed class ObjectStorageRestoreService
{
    private readonly IAssetRepository _assetRepository;
    private readonly IObjectStorageUploadRepository _storageRepository;
    private readonly IObjectStorageRestoreRepository _restoreRepository;
    private readonly ObjectStorageProfileService _profileService;
    private readonly IWorkspaceProvisioner _workspaceProvisioner;
    private readonly IReadOnlyDictionary<ObjectStorageProvider, IObjectStorageAdapter>
        _adapters;

    public ObjectStorageRestoreService(
        IAssetRepository assetRepository,
        IObjectStorageUploadRepository storageRepository,
        IObjectStorageRestoreRepository restoreRepository,
        ObjectStorageProfileService profileService,
        IWorkspaceProvisioner workspaceProvisioner,
        IEnumerable<IObjectStorageAdapter> adapters)
    {
        _assetRepository = assetRepository;
        _storageRepository = storageRepository;
        _restoreRepository = restoreRepository;
        _profileService = profileService;
        _workspaceProvisioner = workspaceProvisioner;
        _adapters = adapters.ToDictionary(adapter => adapter.Provider);
    }

    public async Task<IReadOnlyList<ObjectStorageRestoreCandidate>>
        ListCandidatesAsync(
            IReadOnlyCollection<Guid> assetIds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var profiles = (await _profileService.ListAsync(cancellationToken))
            .ToDictionary(profile => profile.Profile.Id);
        var result = new List<ObjectStorageRestoreCandidate>();
        foreach (var assetId in assetIds.Distinct())
        {
            var sources = await _storageRepository
                .ListObjectStorageRestoreSourcesAsync(assetId, cancellationToken);
            if (sources.Count == 0)
            {
                continue;
            }

            var configured = sources
                .Where(source => profiles.ContainsKey(source.Location.StorageProfileId))
                .Select(source =>
                {
                    var profile = profiles[source.Location.StorageProfileId];
                    return new ConfiguredObjectStorageRestoreSource(
                        source,
                        profile.Profile,
                        profile.HasStoredSecret);
                })
                .ToArray();
            result.Add(new ObjectStorageRestoreCandidate(
                assetId,
                sources[0].OriginalFilename,
                configured));
        }

        return result;
    }

    public async Task<ObjectStorageRestoreResult> RestoreAsync(
        IReadOnlyCollection<ObjectStorageRestoreRequest> requests,
        ObjectStorageRestoreDestination destination,
        IProgress<ObjectStorageRestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(destination);
        if (requests.Count == 0)
        {
            throw new ArgumentException("至少选择一个 OSS 备份。", nameof(requests));
        }

        var uniqueRequests = requests
            .GroupBy(request => request.AssetId)
            .Select(group => group.First())
            .ToArray();
        var deviceId = await _assetRepository.GetOrCreateDeviceIdAsync(
            cancellationToken);
        var targetRoot = await ResolveTargetRootAsync(
            destination,
            deviceId,
            cancellationToken);
        var resolved = await ResolveRequestsAsync(
            uniqueRequests,
            destination.Kind,
            targetRoot,
            cancellationToken);
        RejectDuplicateTargets(resolved);

        var jobId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var totalBytes = resolved
            .Where(item => item.Source is not null && item.ValidationError is null)
            .Aggregate(0L, (total, item) =>
                AddWithoutOverflow(total, item.Source!.Location.Size));
        var auditItems = resolved.Select(item => new ObjectStorageRestoreItem(
            Guid.NewGuid(),
            jobId,
            item.Request.AssetId,
            item.Source?.Location.StorageProfileId ?? Guid.Empty,
            item.Source?.Location.ObjectKey ?? string.Empty,
            item.TargetPath ?? targetRoot,
            RestoreItemStatus.Pending,
            item.Source?.Location.Size ?? 0,
            DownloadedBytes: 0,
            Sha256: null,
            ErrorMessage: item.ValidationError,
            FinishedAt: null)).ToArray();
        var job = new ObjectStorageRestoreJob(
            jobId,
            RestoreJobStatus.Pending,
            destination.Kind,
            targetRoot,
            startedAt,
            FinishedAt: null,
            TotalItems: auditItems.Length,
            CompletedItems: 0,
            FailedItems: 0,
            TotalBytes: totalBytes,
            DownloadedBytes: 0,
            ErrorMessage: null);
        await _restoreRepository.CreateRestoreJobAsync(
            job,
            auditItems,
            cancellationToken);
        job = job with { Status = RestoreJobStatus.Downloading };
        await _restoreRepository.UpdateRestoreJobAsync(job, cancellationToken);

        var restoredBytes = 0L;
        var networkBytes = 0L;
        var results = new List<ObjectStorageRestoreItemResult>(resolved.Count);
        var cancelled = false;
        var connections = new Dictionary<Guid, ObjectStorageConnection>();

        for (var index = 0; index < resolved.Count; index++)
        {
            var current = resolved[index];
            var auditItem = auditItems[index];
            if (current.Source is null ||
                current.TargetPath is null ||
                current.ValidationError is not null)
            {
                var failed = auditItem with
                {
                    Status = RestoreItemStatus.Failed,
                    ErrorMessage = current.ValidationError,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _restoreRepository.SaveRestoreItemAsync(failed, cancellationToken);
                results.Add(ToResult(failed));
                ReportProgress(
                    progress,
                    jobId,
                    resolved.Count,
                    results.Count,
                    totalBytes,
                    restoredBytes,
                    networkBytes,
                    current.TargetPath,
                    current.ValidationError);
                continue;
            }

            var source = current.Source;
            var itemNetworkBytes = 0L;
            var baseNetworkBytes = networkBytes;
            string? temporaryPath = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var connection = await GetConnectionAsync(
                    source.Location.StorageProfileId,
                    connections,
                    cancellationToken);
                if (!_adapters.TryGetValue(connection.Profile.Provider, out var adapter))
                {
                    throw new NotSupportedException(
                        $"尚未安装 {connection.Profile.Provider} 存储适配器。");
                }

                var downloading = auditItem with
                {
                    Status = RestoreItemStatus.Downloading,
                    ErrorMessage = null
                };
                await _restoreRepository.SaveRestoreItemAsync(
                    downloading,
                    cancellationToken);
                ReportProgress(
                    progress,
                    jobId,
                    resolved.Count,
                    results.Count,
                    totalBytes,
                    restoredBytes,
                    networkBytes,
                    source.Location.ObjectKey,
                    "正在核验 OSS 对象");

                var remote = await adapter.StatAsync(
                    connection,
                    source.Location.ObjectKey,
                    cancellationToken);
                await SaveVerificationAsync(source, remote, cancellationToken);
                EnsureRemoteMatches(source, remote);

                var expectedHash = source.Location.Sha256!;
                if (File.Exists(current.TargetPath))
                {
                    var existingHash = await VerifyExistingFileAsync(
                        current.TargetPath,
                        source.Location.Size,
                        expectedHash,
                        cancellationToken);
                    await RegisterLocationAsync(
                        source.AssetId,
                        deviceId,
                        current.TargetPath,
                        destination.Kind,
                        cancellationToken);
                    restoredBytes = AddWithoutOverflow(
                        restoredBytes,
                        source.Location.Size);
                    var reused = downloading with
                    {
                        Status = RestoreItemStatus.Completed,
                        Sha256 = existingHash,
                        ErrorMessage = null,
                        FinishedAt = DateTimeOffset.UtcNow
                    };
                    await _restoreRepository.SaveRestoreItemAsync(
                        reused,
                        cancellationToken);
                    results.Add(ToResult(reused));
                    ReportProgress(
                        progress,
                        jobId,
                        resolved.Count,
                        results.Count,
                        totalBytes,
                        restoredBytes,
                        networkBytes,
                        current.TargetPath,
                        "已复用内容一致的本地文件");
                    continue;
                }

                var targetDirectory = Path.GetDirectoryName(current.TargetPath)
                    ?? throw new InvalidOperationException("恢复目标没有父目录。");
                EnsureSafeDirectory(targetDirectory);
                temporaryPath = Path.Combine(
                    targetDirectory,
                    $".{Path.GetFileName(current.TargetPath)}.{Guid.NewGuid():N}.cdsi-part");
                await using (var output = OpenTemporaryFile(temporaryPath))
                {
                    var downloadProgress = new Progress<ObjectStorageDownloadProgress>(value =>
                    {
                        itemNetworkBytes = Math.Max(itemNetworkBytes, value.TransferredBytes);
                        ReportProgress(
                            progress,
                            jobId,
                            resolved.Count,
                            results.Count,
                            totalBytes,
                            restoredBytes,
                            AddWithoutOverflow(baseNetworkBytes, itemNetworkBytes),
                            source.Location.ObjectKey,
                            value.Message);
                    });
                    var download = await adapter.DownloadAsync(
                        connection,
                        source.Location.ObjectKey,
                        output,
                        downloadProgress,
                        cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                    itemNetworkBytes = Math.Max(
                        itemNetworkBytes,
                        download.DownloadedBytes);
                    await SaveVerificationAsync(
                        source,
                        download.Object,
                        cancellationToken);
                    EnsureRemoteMatches(source, download.Object);
                }

                var verifying = downloading with
                {
                    Status = RestoreItemStatus.Verifying,
                    DownloadedBytes = itemNetworkBytes
                };
                await _restoreRepository.SaveRestoreItemAsync(
                    verifying,
                    cancellationToken);
                ReportProgress(
                    progress,
                    jobId,
                    resolved.Count,
                    results.Count,
                    totalBytes,
                    restoredBytes,
                    AddWithoutOverflow(baseNetworkBytes, itemNetworkBytes),
                    temporaryPath,
                    "正在校验下载文件");
                string restoredHash;
                try
                {
                    restoredHash = await VerifyDownloadedFileAsync(
                        temporaryPath,
                        source.Location.Size,
                        expectedHash,
                        cancellationToken);
                }
                catch (IOException)
                {
                    await SaveLocationStatusAsync(
                        source,
                        StorageVerificationStatus.Unverified,
                        cancellationToken);
                    throw;
                }

                if (File.Exists(current.TargetPath))
                {
                    await VerifyExistingFileAsync(
                        current.TargetPath,
                        source.Location.Size,
                        expectedHash,
                        cancellationToken);
                    File.Delete(temporaryPath);
                    temporaryPath = null;
                }
                else
                {
                    File.Move(temporaryPath, current.TargetPath, overwrite: false);
                    temporaryPath = null;
                    File.SetLastWriteTimeUtc(
                        current.TargetPath,
                        source.AssetModifiedAt.UtcDateTime);
                }

                await RegisterLocationAsync(
                    source.AssetId,
                    deviceId,
                    current.TargetPath,
                    destination.Kind,
                    cancellationToken);
                networkBytes = AddWithoutOverflow(networkBytes, itemNetworkBytes);
                restoredBytes = AddWithoutOverflow(
                    restoredBytes,
                    source.Location.Size);
                var completed = verifying with
                {
                    Status = RestoreItemStatus.Completed,
                    DownloadedBytes = itemNetworkBytes,
                    Sha256 = restoredHash,
                    ErrorMessage = null,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _restoreRepository.SaveRestoreItemAsync(
                    completed,
                    cancellationToken);
                results.Add(ToResult(completed));
            }
            catch (OperationCanceledException)
            {
                networkBytes = AddWithoutOverflow(networkBytes, itemNetworkBytes);
                TryDeleteTemporaryFile(temporaryPath);
                var cancelledItem = auditItem with
                {
                    Status = RestoreItemStatus.Cancelled,
                    DownloadedBytes = itemNetworkBytes,
                    ErrorMessage = "取回已取消。",
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _restoreRepository.SaveRestoreItemAsync(
                    cancelledItem,
                    CancellationToken.None);
                results.Add(ToResult(cancelledItem));
                for (var remainingIndex = index + 1;
                     remainingIndex < auditItems.Length;
                     remainingIndex++)
                {
                    var remaining = auditItems[remainingIndex] with
                    {
                        Status = RestoreItemStatus.Cancelled,
                        ErrorMessage = "任务在处理该文件前已取消。",
                        FinishedAt = DateTimeOffset.UtcNow
                    };
                    await _restoreRepository.SaveRestoreItemAsync(
                        remaining,
                        CancellationToken.None);
                    results.Add(ToResult(remaining));
                }

                cancelled = true;
                break;
            }
            catch (Exception exception)
            {
                networkBytes = AddWithoutOverflow(networkBytes, itemNetworkBytes);
                TryDeleteTemporaryFile(temporaryPath);
                var message = File.Exists(current.TargetPath)
                    ? $"目标文件已保留且未被覆盖: {exception.Message}"
                    : exception.Message;
                var failed = auditItem with
                {
                    Status = RestoreItemStatus.Failed,
                    DownloadedBytes = itemNetworkBytes,
                    ErrorMessage = message,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _restoreRepository.SaveRestoreItemAsync(
                    failed,
                    CancellationToken.None);
                results.Add(ToResult(failed));
            }

            ReportProgress(
                progress,
                jobId,
                resolved.Count,
                results.Count,
                totalBytes,
                restoredBytes,
                networkBytes,
                current.TargetPath,
                null);
        }

        var completedCount = results.Count(item =>
            item.Status == RestoreItemStatus.Completed);
        var failedCount = results.Count(item =>
            item.Status == RestoreItemStatus.Failed);
        var status = cancelled
            ? RestoreJobStatus.Cancelled
            : completedCount == resolved.Count
                ? RestoreJobStatus.Completed
                : completedCount > 0
                    ? RestoreJobStatus.PartiallyCompleted
                    : RestoreJobStatus.Failed;
        var finalJob = job with
        {
            Status = status,
            FinishedAt = DateTimeOffset.UtcNow,
            CompletedItems = completedCount,
            FailedItems = failedCount,
            DownloadedBytes = networkBytes,
            ErrorMessage = failedCount == 0
                ? null
                : $"{failedCount:N0} 个资产未能取回。"
        };
        await _restoreRepository.UpdateRestoreJobAsync(
            finalJob,
            CancellationToken.None);
        return new ObjectStorageRestoreResult(jobId, status, results);
    }

    private async Task<List<ResolvedRestore>> ResolveRequestsAsync(
        IReadOnlyList<ObjectStorageRestoreRequest> requests,
        ObjectStorageRestoreDestinationKind destinationKind,
        string targetRoot,
        CancellationToken cancellationToken)
    {
        var result = new List<ResolvedRestore>(requests.Count);
        foreach (var request in requests)
        {
            var sources = await _storageRepository
                .ListObjectStorageRestoreSourcesAsync(
                    request.AssetId,
                    cancellationToken);
            var source = sources.SingleOrDefault(item =>
                item.Location.Id == request.StorageLocationId);
            var validationError = ValidateSource(source);
            string? targetPath = null;
            if (source is not null)
            {
                targetPath = BuildTargetPath(
                    targetRoot,
                    destinationKind,
                    source.AssetId,
                    source.OriginalFilename);
            }

            result.Add(new ResolvedRestore(
                request,
                source,
                targetPath,
                validationError));
        }

        return result;
    }

    private static string? ValidateSource(ObjectStorageRestoreSource? source)
    {
        if (source is null)
        {
            return "所选 OSS 备份不存在或已不属于该资产。";
        }

        if (source.Location.Status != StorageVerificationStatus.Healthy)
        {
            return "只允许从已通过完整性校验的 OSS 备份取回。";
        }

        if (source.Location.Size != source.AssetSize)
        {
            return "OSS 备份大小与资产索引不一致，请重新备份或核验。";
        }

        if (string.IsNullOrWhiteSpace(source.Location.Sha256))
        {
            return "OSS 备份缺少 SHA-256，不能安全取回。";
        }

        if (source.AssetSha256 is not null &&
            !string.Equals(
                source.AssetSha256,
                source.Location.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return "OSS 备份哈希与资产索引不一致，请重新核验。";
        }

        return null;
    }

    private static void RejectDuplicateTargets(List<ResolvedRestore> resolved)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var duplicates = resolved
            .Where(item => item.ValidationError is null && item.TargetPath is not null)
            .GroupBy(item => item.TargetPath!, comparer)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(comparer);
        for (var index = 0; index < resolved.Count; index++)
        {
            if (resolved[index].TargetPath is not null &&
                duplicates.Contains(resolved[index].TargetPath!))
            {
                resolved[index] = resolved[index] with
                {
                    ValidationError = "多个资产将恢复为同一路径，请改用 CDSI 工作目录或分开取回。"
                };
            }
        }
    }

    private async Task<string> ResolveTargetRootAsync(
        ObjectStorageRestoreDestination destination,
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (destination.Kind == ObjectStorageRestoreDestinationKind.ManagedWorkspace)
        {
            var workspace = await _assetRepository.GetManagedWorkspaceAsync(
                deviceId,
                cancellationToken)
                ?? throw new InvalidOperationException("尚未配置 CDSI 工作目录。");
            var layout = await _workspaceProvisioner.ProvisionAsync(
                workspace.Path,
                cancellationToken);
            return layout.AssetsPath;
        }

        if (string.IsNullOrWhiteSpace(destination.DirectoryPath))
        {
            throw new ArgumentException("请选择恢复目录。", nameof(destination));
        }

        var path = Path.GetFullPath(destination.DirectoryPath);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"恢复目录不存在: {path}");
        }

        EnsureSafeDirectory(path);
        return NormalizeDirectoryPath(path);
    }

    private async Task<ObjectStorageConnection> GetConnectionAsync(
        Guid profileId,
        IDictionary<Guid, ObjectStorageConnection> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(profileId, out var connection))
        {
            return connection;
        }

        connection = await _profileService.GetConnectionAsync(
            profileId,
            cancellationToken);
        cache.Add(profileId, connection);
        return connection;
    }

    private async Task SaveVerificationAsync(
        ObjectStorageRestoreSource source,
        ObjectStorageObjectInfo? remote,
        CancellationToken cancellationToken)
    {
        var status = DetermineVerificationStatus(source, remote);
        await _storageRepository.SaveObjectStorageLocationAsync(
            source.Location with
            {
                Status = status,
                Size = remote?.Size ?? source.Location.Size,
                Sha256 = remote?.Sha256 ?? source.Location.Sha256,
                ETag = remote?.ETag ?? source.Location.ETag,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastVerifiedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    private async Task SaveLocationStatusAsync(
        ObjectStorageRestoreSource source,
        StorageVerificationStatus status,
        CancellationToken cancellationToken)
    {
        await _storageRepository.SaveObjectStorageLocationAsync(
            source.Location with
            {
                Status = status,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastVerifiedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    private static StorageVerificationStatus DetermineVerificationStatus(
        ObjectStorageRestoreSource source,
        ObjectStorageObjectInfo? remote)
    {
        return remote is null
            ? StorageVerificationStatus.Missing
            : remote.Size != source.Location.Size
                ? StorageVerificationStatus.SizeMismatch
                : !string.Equals(
                    remote.Sha256,
                    source.Location.Sha256,
                    StringComparison.OrdinalIgnoreCase)
                    ? StorageVerificationStatus.ChecksumMismatch
                    : StorageVerificationStatus.Healthy;
    }

    private static void EnsureRemoteMatches(
        ObjectStorageRestoreSource source,
        ObjectStorageObjectInfo? remote)
    {
        var status = DetermineVerificationStatus(source, remote);
        if (status == StorageVerificationStatus.Healthy)
        {
            return;
        }

        throw status switch
        {
            StorageVerificationStatus.Missing =>
                new FileNotFoundException("OSS 对象不存在。", source.Location.ObjectKey),
            StorageVerificationStatus.SizeMismatch =>
                new IOException("OSS 对象大小与已登记备份不一致。"),
            _ => new IOException("OSS 对象 SHA-256 与已登记备份不一致。")
        };
    }

    private async Task RegisterLocationAsync(
        Guid assetId,
        string deviceId,
        string targetPath,
        ObjectStorageRestoreDestinationKind destinationKind,
        CancellationToken cancellationToken)
    {
        var ownership = destinationKind ==
            ObjectStorageRestoreDestinationKind.ManagedWorkspace
            ? AssetLocationOwnership.Managed
            : AssetLocationOwnership.External;
        await _assetRepository.RegisterLocalLocationAsync(
            assetId,
            deviceId,
            targetPath,
            ownership,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private static string BuildTargetPath(
        string targetRoot,
        ObjectStorageRestoreDestinationKind destinationKind,
        Guid assetId,
        string originalFilename)
    {
        var filename = Path.GetFileName(originalFilename);
        if (string.IsNullOrWhiteSpace(filename) ||
            !string.Equals(filename, originalFilename, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("资产原始文件名无效。");
        }

        var parent = destinationKind ==
            ObjectStorageRestoreDestinationKind.ManagedWorkspace
            ? Path.Combine(targetRoot, assetId.ToString("N"))
            : targetRoot;
        var normalizedRoot = NormalizeDirectoryPath(targetRoot);
        var target = Path.GetFullPath(Path.Combine(parent, filename));
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ||
            normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("恢复目标超出所选目录。");
        }

        return target;
    }

    private static async Task<string> VerifyExistingFileAsync(
        string path,
        long expectedSize,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.Length != expectedSize)
        {
            throw new IOException("目标文件已存在且内容不同，未覆盖现有文件。");
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("目标文件不能是符号链接。");
        }

        var hash = await CalculateSha256Async(path, cancellationToken);
        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("目标文件已存在且内容不同，未覆盖现有文件。");
        }

        return hash;
    }

    private static async Task<string> VerifyDownloadedFileAsync(
        string path,
        long expectedSize,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists || info.Length != expectedSize)
        {
            throw new IOException("下载文件大小校验失败。");
        }

        var hash = await CalculateSha256Async(path, cancellationToken);
        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("下载文件 SHA-256 校验失败。");
        }

        return hash;
    }

    private static async Task<string> CalculateSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 1024 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static FileStream OpenTemporaryFile(string path)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 1024 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
    }

    private static void EnsureSafeDirectory(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"恢复目录不能位于符号链接或 junction 中: {current.FullName}");
            }

            current = current.Parent;
        }

        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"恢复目录不能是符号链接或 junction: {path}");
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(
                fullPath,
                root,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            ? fullPath
            : fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
    }

    private static void TryDeleteTemporaryFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // The audit remains authoritative; a locked partial file is never registered.
        }
    }

    private static void ReportProgress(
        IProgress<ObjectStorageRestoreProgress>? progress,
        Guid jobId,
        int totalItems,
        int processedItems,
        long totalBytes,
        long restoredBytes,
        long networkBytes,
        string? currentPath,
        string? message)
    {
        progress?.Report(new ObjectStorageRestoreProgress(
            jobId,
            totalItems,
            processedItems,
            totalBytes,
            Math.Min(restoredBytes, totalBytes),
            networkBytes,
            currentPath,
            message));
    }

    private static ObjectStorageRestoreItemResult ToResult(
        ObjectStorageRestoreItem item)
    {
        return new ObjectStorageRestoreItemResult(
            item.AssetId,
            item.ObjectKey,
            item.TargetPath,
            item.Status,
            item.DownloadedBytes,
            item.ErrorMessage);
    }

    private static long AddWithoutOverflow(long left, long right)
    {
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private sealed record ResolvedRestore(
        ObjectStorageRestoreRequest Request,
        ObjectStorageRestoreSource? Source,
        string? TargetPath,
        string? ValidationError);
}
