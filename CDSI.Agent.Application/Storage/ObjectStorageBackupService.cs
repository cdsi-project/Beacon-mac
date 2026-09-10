using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Core.Transfers;

namespace CDSI.Agent.Application.Storage;

public sealed class ObjectStorageBackupService
{
    private readonly IAssetRepository _assetRepository;
    private readonly IObjectStorageUploadRepository _uploadRepository;
    private readonly ObjectStorageProfileService _profileService;
    private readonly IFileFingerprintService _fingerprintService;
    private readonly IReadOnlyDictionary<ObjectStorageProvider, IObjectStorageAdapter>
        _adapters;

    public ObjectStorageBackupService(
        IAssetRepository assetRepository,
        IObjectStorageUploadRepository uploadRepository,
        ObjectStorageProfileService profileService,
        IFileFingerprintService fingerprintService,
        IEnumerable<IObjectStorageAdapter> adapters)
    {
        _assetRepository = assetRepository;
        _uploadRepository = uploadRepository;
        _profileService = profileService;
        _fingerprintService = fingerprintService;
        _adapters = adapters.ToDictionary(adapter => adapter.Provider);
    }

    public async Task<ObjectStorageBackupResult> BackupAsync(
        IReadOnlyCollection<ObjectStorageBackupRequest> requests,
        Guid storageProfileId,
        IProgress<ObjectStorageBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("至少选择一个资产位置。", nameof(requests));
        }

        var connection = await _profileService.GetConnectionAsync(
            storageProfileId,
            cancellationToken);
        if (!_adapters.TryGetValue(connection.Profile.Provider, out var adapter))
        {
            throw new NotSupportedException(
                $"尚未安装 {connection.Profile.Provider} 存储适配器。");
        }

        var deviceId = await _assetRepository.GetOrCreateDeviceIdAsync(
            cancellationToken);
        var resolved = await ResolveSourcesAsync(
            requests,
            deviceId,
            cancellationToken);
        var jobId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var totalBytes = resolved
            .Where(item => item.Source is not null)
            .Aggregate(0L, (total, item) =>
                AddWithoutOverflow(total, item.Source!.Size));
        var auditItems = resolved.Select(item => new ObjectStorageUploadItem(
            Guid.NewGuid(),
            jobId,
            item.Request.AssetId,
            Path.GetFullPath(item.Request.SourcePath),
            item.ObjectKey,
            UploadItemStatus.Pending,
            item.Source?.Size ?? 0,
            UploadedBytes: 0,
            ETag: null,
            ErrorMessage: item.ValidationError,
            FinishedAt: null)).ToArray();
        var job = new ObjectStorageUploadJob(
            jobId,
            storageProfileId,
            UploadJobStatus.Pending,
            startedAt,
            FinishedAt: null,
            TotalItems: auditItems.Length,
            CompletedItems: 0,
            FailedItems: 0,
            TotalBytes: totalBytes,
            UploadedBytes: 0,
            ErrorMessage: null);
        await _uploadRepository.CreateUploadJobAsync(
            job,
            auditItems,
            cancellationToken);
        job = job with { Status = UploadJobStatus.Uploading };
        await _uploadRepository.UpdateUploadJobAsync(job, cancellationToken);

        var processedBytes = 0L;
        var networkTransferredBytes = 0L;
        var results = new List<ObjectStorageBackupItemResult>(resolved.Count);
        var cancelled = false;
        for (var index = 0; index < resolved.Count; index++)
        {
            var current = resolved[index];
            var auditItem = auditItems[index];
            if (current.Source is null || current.ValidationError is not null)
            {
                var failed = auditItem with
                {
                    Status = UploadItemStatus.Failed,
                    ErrorMessage = current.ValidationError,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _uploadRepository.SaveUploadItemAsync(failed, cancellationToken);
                results.Add(ToResult(failed));
                ReportProgress(
                    progress,
                    jobId,
                    resolved.Count,
                    results.Count,
                    totalBytes,
                    processedBytes,
                    networkTransferredBytes,
                    current.Request.SourcePath,
                    current.ValidationError);
                continue;
            }

            var itemBaseBytes = processedBytes;
            var itemBaseNetworkBytes = networkTransferredBytes;
            var itemProgressBytes = 0L;
            var itemNetworkBytes = 0L;
            try
            {
                var source = current.Source;
                var sha256 = source.Sha256;
                if (string.IsNullOrWhiteSpace(sha256))
                {
                    ReportProgress(
                        progress,
                        jobId,
                        resolved.Count,
                        results.Count,
                        totalBytes,
                        processedBytes,
                        networkTransferredBytes,
                        source.Path,
                        "正在计算 SHA-256");
                    var discovered = new DiscoveredFile(
                        source.Path,
                        source.OriginalFilename,
                        source.Extension,
                        MimeType: null,
                        source.Size,
                        CreatedAt: source.ModifiedAt,
                        source.ModifiedAt);
                    var fingerprint = await _fingerprintService.CalculateAsync(
                        discovered,
                        hashProgress => ReportProgress(
                            progress,
                            jobId,
                            resolved.Count,
                            results.Count,
                            totalBytes,
                            processedBytes,
                            networkTransferredBytes,
                            source.Path,
                            $"正在计算 SHA-256 · {FormatProgress(hashProgress.BytesProcessed, hashProgress.TotalBytes)}"),
                        cancellationToken);
                    sha256 = fingerprint.Sha256;
                    var saved = await _assetRepository.SaveSha256Async(
                        source.AssetId,
                        source.Size,
                        source.ModifiedAt,
                        sha256,
                        cancellationToken);
                    if (!saved)
                    {
                        throw new IOException("资产索引在哈希计算期间发生变化，请重新扫描。");
                    }
                }

                var uploading = auditItem with
                {
                    Status = UploadItemStatus.Uploading,
                    ErrorMessage = null
                };
                await _uploadRepository.SaveUploadItemAsync(
                    uploading,
                    cancellationToken);
                ReportProgress(
                    progress,
                    jobId,
                    resolved.Count,
                    results.Count,
                    totalBytes,
                    processedBytes,
                    networkTransferredBytes,
                    source.Path,
                    "正在检查 OSS 目标");

                var existing = await adapter.StatAsync(
                    connection,
                    current.ObjectKey,
                    cancellationToken);
                ObjectStorageObjectInfo? verifiedObject;
                if (existing is not null)
                {
                    await SaveVerificationAsync(
                        source.AssetId,
                        storageProfileId,
                        current.ObjectKey,
                        existing,
                        source.Size,
                        sha256,
                        cancellationToken);
                    verifiedObject = EnsureObjectMatchesSource(
                        existing,
                        source.Size,
                        sha256);
                }
                else
                {
                    var session = await LoadValidSessionAsync(
                        adapter,
                        connection,
                        source,
                        current.ObjectKey,
                        cancellationToken);
                    var transferProgress = new InlineProgress<ObjectStorageTransferProgress>(
                        value =>
                        {
                            itemProgressBytes = Math.Min(value.TransferredBytes, source.Size);
                            itemNetworkBytes = Math.Min(
                                value.CurrentRunTransferredBytes,
                                source.Size);
                            ReportProgress(
                                progress,
                                jobId,
                                resolved.Count,
                                results.Count,
                                totalBytes,
                                AddWithoutOverflow(itemBaseBytes, itemProgressBytes),
                                AddWithoutOverflow(
                                    itemBaseNetworkBytes,
                                    itemNetworkBytes),
                                source.Path,
                                value.Message);
                        });
                    await adapter.UploadAsync(
                        new ObjectStorageTransferRequest(
                            connection,
                            source.AssetId,
                            source.Path,
                            current.ObjectKey,
                            source.Size,
                            source.ModifiedAt,
                            sha256,
                            session),
                        (checkpoint, token) =>
                            _uploadRepository.SaveMultipartUploadSessionAsync(
                                checkpoint,
                                token),
                        transferProgress,
                        cancellationToken);

                    var verifying = uploading with
                    {
                        Status = UploadItemStatus.Verifying,
                        UploadedBytes = source.Size
                    };
                    await _uploadRepository.SaveUploadItemAsync(
                        verifying,
                        cancellationToken);
                    job = job with { Status = UploadJobStatus.Verifying };
                    await _uploadRepository.UpdateUploadJobAsync(
                        job,
                        cancellationToken);
                    ReportProgress(
                        progress,
                        jobId,
                        resolved.Count,
                        results.Count,
                        totalBytes,
                        AddWithoutOverflow(itemBaseBytes, source.Size),
                        AddWithoutOverflow(
                            itemBaseNetworkBytes,
                            itemNetworkBytes),
                        source.Path,
                        "正在校验 OSS 备份");
                    verifiedObject = await adapter.StatAsync(
                        connection,
                        current.ObjectKey,
                        cancellationToken);
                    await SaveVerificationAsync(
                        source.AssetId,
                        storageProfileId,
                        current.ObjectKey,
                        verifiedObject,
                        source.Size,
                        sha256,
                        cancellationToken);
                    verifiedObject = EnsureObjectMatchesSource(
                        verifiedObject,
                        source.Size,
                        sha256);
                }

                var now = DateTimeOffset.UtcNow;
                await _uploadRepository.DeleteMultipartUploadSessionAsync(
                    storageProfileId,
                    current.ObjectKey,
                    cancellationToken);

                processedBytes = AddWithoutOverflow(processedBytes, source.Size);
                var completed = auditItem with
                {
                    Status = UploadItemStatus.Completed,
                    UploadedBytes = source.Size,
                    ETag = verifiedObject.ETag,
                    ErrorMessage = null,
                    FinishedAt = now
                };
                await _uploadRepository.SaveUploadItemAsync(
                    completed,
                    cancellationToken);
                results.Add(ToResult(completed));
                job = job with { Status = UploadJobStatus.Uploading };
            }
            catch (OperationCanceledException)
            {
                var cancelledItem = auditItem with
                {
                    Status = UploadItemStatus.Cancelled,
                    UploadedBytes = itemProgressBytes,
                    ErrorMessage = "备份已取消；已上传分片会保留供下次继续。",
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _uploadRepository.SaveUploadItemAsync(
                    cancelledItem,
                    CancellationToken.None);
                results.Add(ToResult(cancelledItem));
                for (var remainingIndex = index + 1;
                     remainingIndex < auditItems.Length;
                     remainingIndex++)
                {
                    var remaining = auditItems[remainingIndex] with
                    {
                        Status = UploadItemStatus.Cancelled,
                        ErrorMessage = "备份在处理该文件前已取消。",
                        FinishedAt = DateTimeOffset.UtcNow
                    };
                    await _uploadRepository.SaveUploadItemAsync(
                        remaining,
                        CancellationToken.None);
                    results.Add(ToResult(remaining));
                }

                cancelled = true;
                break;
            }
            catch (Exception exception)
            {
                var failed = auditItem with
                {
                    Status = UploadItemStatus.Failed,
                    UploadedBytes = itemProgressBytes,
                    ErrorMessage = exception.Message,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _uploadRepository.SaveUploadItemAsync(
                    failed,
                    CancellationToken.None);
                results.Add(ToResult(failed));
            }

            networkTransferredBytes = AddWithoutOverflow(
                itemBaseNetworkBytes,
                itemNetworkBytes);
            ReportProgress(
                progress,
                jobId,
                resolved.Count,
                results.Count,
                totalBytes,
                processedBytes,
                networkTransferredBytes,
                current.Source.Path,
                null);
        }

        var completedCount = results.Count(item =>
            item.Status == UploadItemStatus.Completed);
        var failedCount = results.Count(item =>
            item.Status == UploadItemStatus.Failed);
        var status = cancelled
            ? UploadJobStatus.Cancelled
            : completedCount == resolved.Count
                ? UploadJobStatus.Completed
                : completedCount > 0
                    ? UploadJobStatus.PartiallyCompleted
                    : UploadJobStatus.Failed;
        var finalJob = job with
        {
            Status = status,
            FinishedAt = DateTimeOffset.UtcNow,
            CompletedItems = completedCount,
            FailedItems = failedCount,
            UploadedBytes = processedBytes,
            ErrorMessage = results.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.ErrorMessage))?.ErrorMessage
        };
        await _uploadRepository.UpdateUploadJobAsync(
            finalJob,
            CancellationToken.None);

        return new ObjectStorageBackupResult(
            jobId,
            storageProfileId,
            status,
            results);
    }

    private async Task<List<ResolvedBackup>> ResolveSourcesAsync(
        IReadOnlyCollection<ObjectStorageBackupRequest> requests,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var result = new List<ResolvedBackup>();
        foreach (var group in requests.GroupBy(request => request.AssetId))
        {
            LocalAssetTransferSource? source = null;
            ObjectStorageBackupRequest? selectedRequest = null;
            foreach (var request in group)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
                var normalized = request with
                {
                    SourcePath = Path.GetFullPath(request.SourcePath)
                };
                selectedRequest ??= normalized;
                source = await _assetRepository.GetLocalAssetTransferSourceAsync(
                    normalized.AssetId,
                    deviceId,
                    normalized.SourcePath,
                    cancellationToken);
                if (source is not null)
                {
                    selectedRequest = normalized;
                    break;
                }
            }

            var requestToUse = selectedRequest!;
            var objectName = requestToUse.ObjectName ??
                Path.GetFileName(source?.Path ?? requestToUse.SourcePath);
            string objectKey;
            string? objectKeyError;
            var hasValidObjectKey = requestToUse.ObjectDirectory is null
                ? ObjectStorageObjectKey.TryCreateForAsset(
                    requestToUse.AssetId,
                    objectName,
                    out objectKey,
                    out objectKeyError)
                : ObjectStorageObjectKey.TryCreateForDirectory(
                    requestToUse.ObjectDirectory,
                    objectName,
                    out objectKey,
                    out objectKeyError);
            result.Add(new ResolvedBackup(
                requestToUse,
                source,
                objectKey,
                source is null
                    ? "该位置不是当前设备上可用的已登记资产。"
                    : hasValidObjectKey
                        ? null
                        : objectKeyError));
        }

        var duplicateObjectKeys = result
            .Where(item => item.ValidationError is null)
            .GroupBy(item => item.ObjectKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return result.Select(item =>
            item.ValidationError is null && duplicateObjectKeys.Contains(item.ObjectKey)
                ? item with
                {
                    ValidationError =
                        $"多个资产将写入同一个 OSS 对象：{item.ObjectKey}。请处理清单中的同名文件后重试。"
                }
                : item).ToList();
    }

    private async Task<MultipartUploadSession?> LoadValidSessionAsync(
        IObjectStorageAdapter adapter,
        ObjectStorageConnection connection,
        LocalAssetTransferSource source,
        string objectKey,
        CancellationToken cancellationToken)
    {
        var session = await _uploadRepository.GetMultipartUploadSessionAsync(
            connection.Profile.Id,
            objectKey,
            cancellationToken);
        if (session is null)
        {
            return null;
        }

        if (session.AssetId == source.AssetId &&
            PathsEqual(session.SourcePath, source.Path) &&
            session.SourceSize == source.Size &&
            session.SourceModifiedAt == source.ModifiedAt)
        {
            return session;
        }

        await adapter.AbortMultipartUploadAsync(
            connection,
            session,
            cancellationToken);
        await _uploadRepository.DeleteMultipartUploadSessionAsync(
            connection.Profile.Id,
            objectKey,
            cancellationToken);
        return null;
    }

    private async Task SaveVerificationAsync(
        Guid assetId,
        Guid storageProfileId,
        string objectKey,
        ObjectStorageObjectInfo? value,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var status = value is null
            ? StorageVerificationStatus.Missing
            : value.Size != expectedSize
                ? StorageVerificationStatus.SizeMismatch
                : string.IsNullOrWhiteSpace(value.Sha256) ||
                  !string.Equals(
                      value.Sha256,
                      expectedSha256,
                      StringComparison.OrdinalIgnoreCase)
                    ? StorageVerificationStatus.ChecksumMismatch
                    : StorageVerificationStatus.Healthy;
        var now = DateTimeOffset.UtcNow;
        var oldLocation = await _uploadRepository.GetObjectStorageLocationAsync(
            assetId,
            storageProfileId,
            objectKey,
            cancellationToken);
        await _uploadRepository.SaveObjectStorageLocationAsync(
            new ObjectStorageLocation(
                oldLocation?.Id ?? Guid.NewGuid(),
                assetId,
                storageProfileId,
                objectKey,
                status,
                value?.Size ?? 0,
                value?.Sha256,
                value?.ETag,
                oldLocation?.CreatedAt ?? now,
                now,
                now),
            cancellationToken);
    }

    private static ObjectStorageObjectInfo EnsureObjectMatchesSource(
        ObjectStorageObjectInfo? value,
        long expectedSize,
        string expectedSha256)
    {
        if (value is null)
        {
            throw new IOException("OSS 上传完成后未找到目标对象。");
        }

        if (value.Size != expectedSize)
        {
            throw new IOException(
                "OSS 目标对象已存在，但大小与本地资产不一致；为避免覆盖，已停止备份。");
        }

        if (string.IsNullOrWhiteSpace(value.Sha256) ||
            !string.Equals(
                value.Sha256,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "OSS 目标对象缺少匹配的 SHA-256；为避免覆盖，已停止备份。");
        }

        return value;
    }

    private static void ReportProgress(
        IProgress<ObjectStorageBackupProgress>? progress,
        Guid jobId,
        int totalItems,
        int processedItems,
        long totalBytes,
        long uploadedBytes,
        long networkTransferredBytes,
        string? currentPath,
        string? message)
    {
        progress?.Report(new ObjectStorageBackupProgress(
            jobId,
            totalItems,
            processedItems,
            totalBytes,
            Math.Min(uploadedBytes, totalBytes),
            Math.Min(networkTransferredBytes, totalBytes),
            currentPath,
            message));
    }

    private static string FormatProgress(long completed, long total)
    {
        return total <= 0 ? "0%" : $"{completed * 100d / total:N0}%";
    }

    private static ObjectStorageBackupItemResult ToResult(
        ObjectStorageUploadItem item)
    {
        return new ObjectStorageBackupItemResult(
            item.AssetId,
            item.SourcePath,
            item.ObjectKey,
            item.Status,
            item.UploadedBytes,
            item.ErrorMessage);
    }

    private static long AddWithoutOverflow(long left, long right)
    {
        return long.MaxValue - left < right ? long.MaxValue : left + right;
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            handler(value);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private sealed record ResolvedBackup(
        ObjectStorageBackupRequest Request,
        LocalAssetTransferSource? Source,
        string ObjectKey,
        string? ValidationError);
}
