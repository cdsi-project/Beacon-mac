using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Transfers;

namespace CDSI.Agent.Application.Transfers;

public sealed class ManagedAssetTransferService
{
    private readonly IAssetRepository _repository;
    private readonly IWorkspaceProvisioner _workspaceProvisioner;
    private readonly IManagedFileTransfer _fileTransfer;

    public ManagedAssetTransferService(
        IAssetRepository repository,
        IWorkspaceProvisioner workspaceProvisioner,
        IManagedFileTransfer fileTransfer)
    {
        _repository = repository;
        _workspaceProvisioner = workspaceProvisioner;
        _fileTransfer = fileTransfer;
    }

    public async Task<ManagedAssetTransferResult> TransferAsync(
        IReadOnlyCollection<ManagedAssetTransferRequest> requests,
        ManagedAssetTransferAction action,
        IProgress<ManagedAssetTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            throw new ArgumentException("至少选择一个资产位置。", nameof(requests));
        }

        var distinctRequests = Deduplicate(requests);
        var deviceId = await _repository.GetOrCreateDeviceIdAsync(cancellationToken);
        var workspace = await _repository.GetManagedWorkspaceAsync(
            deviceId,
            cancellationToken)
            ?? throw new InvalidOperationException("尚未配置 CDSI 工作目录。");
        var layout = await _workspaceProvisioner.ProvisionAsync(
            workspace.Path,
            cancellationToken);
        var operationId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var resolvedItems = new List<ResolvedTransfer>(distinctRequests.Count);

        foreach (var request in distinctRequests)
        {
            var source = await _repository.GetLocalAssetTransferSourceAsync(
                request.AssetId,
                deviceId,
                request.SourcePath,
                cancellationToken);
            if (source is null)
            {
                resolvedItems.Add(new ResolvedTransfer(
                    request,
                    null,
                    null,
                    "该位置不是当前设备上可用的已登记资产。"));
                continue;
            }

            var targetPath = BuildTargetPath(layout.AssetsPath, source);
            resolvedItems.Add(new ResolvedTransfer(
                request,
                source,
                targetPath,
                null));
        }

        var auditItems = resolvedItems.Select(item => new FileOperationItemRecord(
            Guid.NewGuid(),
            operationId,
            item.Request.AssetId,
            Path.GetFullPath(item.Request.SourcePath),
            item.TargetPath,
            FileOperationItemStatus.Pending,
            SourceDeleted: false,
            Sha256: null,
            ErrorMessage: item.ValidationError,
            FinishedAt: null)).ToArray();
        var operation = new FileOperationRecord(
            operationId,
            action,
            FileOperationStatus.Running,
            startedAt,
            FinishedAt: null,
            TotalItems: auditItems.Length,
            CompletedItems: 0,
            FailedItems: 0,
            ErrorMessage: null);
        await _repository.CreateFileOperationAsync(
            operation,
            auditItems,
            cancellationToken);

        var totalBytes = resolvedItems
            .Where(item => item.Source is not null)
            .Aggregate(
                0L,
                (total, item) => AddWithoutOverflow(total, item.Source!.Size));
        var processedBytes = 0L;
        var results = new List<ManagedAssetTransferItemResult>(resolvedItems.Count);
        var cancelled = false;

        for (var index = 0; index < resolvedItems.Count; index++)
        {
            var resolved = resolvedItems[index];
            var auditItem = auditItems[index];
            if (resolved.ValidationError is not null ||
                resolved.Source is null ||
                resolved.TargetPath is null)
            {
                var failed = auditItem with
                {
                    Status = FileOperationItemStatus.Failed,
                    ErrorMessage = resolved.ValidationError,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _repository.SaveFileOperationItemAsync(failed, cancellationToken);
                results.Add(ToResult(failed));
                ReportProgress(
                    progress,
                    operationId,
                    resolvedItems.Count,
                    results.Count,
                    totalBytes,
                    processedBytes,
                    resolved.Request.SourcePath,
                    resolved.ValidationError);
                continue;
            }

            var itemBaseBytes = processedBytes;
            try
            {
                ReportProgress(
                    progress,
                    operationId,
                    resolvedItems.Count,
                    results.Count,
                    totalBytes,
                    processedBytes,
                    resolved.Source.Path,
                    action == ManagedAssetTransferAction.Move
                        ? "正在安全移动"
                        : "正在复制");
                var copy = await _fileTransfer.CopyAndVerifyAsync(
                    resolved.Source,
                    resolved.TargetPath,
                    bytes => ReportProgress(
                        progress,
                        operationId,
                        resolvedItems.Count,
                        results.Count,
                        totalBytes,
                        AddWithoutOverflow(itemBaseBytes, bytes),
                        resolved.Source.Path,
                        "正在复制并校验"),
                    cancellationToken);

                var hashSaved = await _repository.SaveSha256Async(
                    resolved.Source.AssetId,
                    resolved.Source.Size,
                    resolved.Source.ModifiedAt,
                    copy.Sha256,
                    cancellationToken);
                if (!hashSaved)
                {
                    throw new IOException("资产索引在复制期间发生变化，请重新扫描。");
                }

                await _repository.RegisterManagedLocalLocationAsync(
                    resolved.Source.AssetId,
                    deviceId,
                    resolved.TargetPath,
                    DateTimeOffset.UtcNow,
                    cancellationToken);

                var sourceDeleted = false;
                if (action == ManagedAssetTransferAction.Move &&
                    !PathsEqual(resolved.Source.Path, resolved.TargetPath))
                {
                    await _fileTransfer.DeleteSourceAsync(
                        resolved.Source.Path,
                        cancellationToken);
                    sourceDeleted = true;
                    await _repository.MarkLocalLocationMissingAsync(
                        deviceId,
                        resolved.Source.Path,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                }

                processedBytes = AddWithoutOverflow(processedBytes, resolved.Source.Size);
                var completed = auditItem with
                {
                    TargetPath = resolved.TargetPath,
                    Status = FileOperationItemStatus.Completed,
                    SourceDeleted = sourceDeleted,
                    Sha256 = copy.Sha256,
                    ErrorMessage = null,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _repository.SaveFileOperationItemAsync(completed, cancellationToken);
                results.Add(ToResult(completed));
            }
            catch (OperationCanceledException)
            {
                var cancelledItem = auditItem with
                {
                    TargetPath = resolved.TargetPath,
                    Status = FileOperationItemStatus.Cancelled,
                    ErrorMessage = "操作已取消。",
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _repository.SaveFileOperationItemAsync(
                    cancelledItem,
                    CancellationToken.None);
                results.Add(ToResult(cancelledItem));
                for (var remainingIndex = index + 1;
                     remainingIndex < auditItems.Length;
                     remainingIndex++)
                {
                    var remainingItem = auditItems[remainingIndex] with
                    {
                        Status = FileOperationItemStatus.Cancelled,
                        ErrorMessage = "操作在处理该文件前已取消。",
                        FinishedAt = DateTimeOffset.UtcNow
                    };
                    await _repository.SaveFileOperationItemAsync(
                        remainingItem,
                        CancellationToken.None);
                    results.Add(ToResult(remainingItem));
                }

                cancelled = true;
                break;
            }
            catch (Exception exception)
            {
                var sourceDeleted = !File.Exists(resolved.Source.Path);
                var message = sourceDeleted
                    ? $"源文件已删除，但索引更新未完成: {exception.Message}"
                    : File.Exists(resolved.TargetPath)
                        ? $"工作目录副本已保留，源文件未删除: {exception.Message}"
                        : exception.Message;
                var failed = auditItem with
                {
                    TargetPath = resolved.TargetPath,
                    Status = FileOperationItemStatus.Failed,
                    SourceDeleted = sourceDeleted,
                    ErrorMessage = message,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await _repository.SaveFileOperationItemAsync(
                    failed,
                    CancellationToken.None);
                results.Add(ToResult(failed));
            }

            ReportProgress(
                progress,
                operationId,
                resolvedItems.Count,
                results.Count,
                totalBytes,
                processedBytes,
                resolved.Source.Path,
                null);
        }

        var completedCount = results.Count(item =>
            item.Status == FileOperationItemStatus.Completed);
        var failedCount = results.Count(item =>
            item.Status == FileOperationItemStatus.Failed);
        var status = cancelled
            ? FileOperationStatus.Cancelled
            : completedCount == resolvedItems.Count
                ? FileOperationStatus.Completed
                : completedCount > 0
                    ? FileOperationStatus.PartiallyCompleted
                    : FileOperationStatus.Failed;
        var finalOperation = operation with
        {
            Status = status,
            FinishedAt = DateTimeOffset.UtcNow,
            CompletedItems = completedCount,
            FailedItems = failedCount,
            ErrorMessage = results.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.ErrorMessage))?.ErrorMessage
        };
        await _repository.UpdateFileOperationAsync(
            finalOperation,
            CancellationToken.None);

        return new ManagedAssetTransferResult(
            operationId,
            action,
            status,
            results);
    }

    private static List<ManagedAssetTransferRequest> Deduplicate(
        IReadOnlyCollection<ManagedAssetTransferRequest> requests)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        var result = new List<ManagedAssetTransferRequest>(requests.Count);
        foreach (var request in requests)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
            var normalizedPath = Path.GetFullPath(request.SourcePath);
            if (paths.Add(normalizedPath))
            {
                result.Add(request with { SourcePath = normalizedPath });
            }
        }

        return result;
    }

    private static string BuildTargetPath(
        string assetsRoot,
        LocalAssetTransferSource source)
    {
        var normalizedRoot = Path.GetFullPath(assetsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var filename = Path.GetFileName(source.Path);
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new InvalidOperationException("无法确定源文件名。");
        }

        var targetPath = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            source.AssetId.ToString("N"),
            filename));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("目标路径超出 CDSI 工作目录。");
        }

        return targetPath;
    }

    private static void ReportProgress(
        IProgress<ManagedAssetTransferProgress>? progress,
        Guid operationId,
        int totalItems,
        int processedItems,
        long totalBytes,
        long processedBytes,
        string? currentPath,
        string? message)
    {
        progress?.Report(new ManagedAssetTransferProgress(
            operationId,
            totalItems,
            processedItems,
            totalBytes,
            Math.Min(processedBytes, totalBytes),
            currentPath,
            message));
    }

    private static ManagedAssetTransferItemResult ToResult(
        FileOperationItemRecord item)
    {
        return new ManagedAssetTransferItemResult(
            item.AssetId,
            item.SourcePath,
            item.TargetPath,
            item.Status,
            item.SourceDeleted,
            item.ErrorMessage);
    }

    private static long AddWithoutOverflow(long left, long right)
    {
        return long.MaxValue - left < right ? long.MaxValue : left + right;
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

    private sealed record ResolvedTransfer(
        ManagedAssetTransferRequest Request,
        LocalAssetTransferSource? Source,
        string? TargetPath,
        string? ValidationError);
}
