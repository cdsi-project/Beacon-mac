using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Duplicates;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Application.Scanning;

public sealed class ScanApplicationService
{
    private const int BatchSize = 200;

    private readonly IFileScanner _scanner;
    private readonly IAssetRepository _repository;

    public ScanApplicationService(
        IFileScanner scanner,
        IAssetRepository repository)
    {
        _scanner = scanner;
        _repository = repository;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return _repository.InitializeAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AssetListItem>> ListAssetsAsync(
        int limit = 5_000,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return _repository.ListAssetsAsync(limit, offset, cancellationToken);
    }

    public Task<IReadOnlyList<AssetListItem>> ListAssetsAsync(
        AssetListFilter filter,
        int limit = 5_000,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (limit is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return _repository.ListAssetsAsync(
            filter,
            limit,
            offset,
            cancellationToken);
    }

    public Task<long> GetAssetListCountAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.GetAssetListCountAsync(cancellationToken);
    }

    public Task<long> GetAssetListCountAsync(
        AssetListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return _repository.GetAssetListCountAsync(filter, cancellationToken);
    }

    public Task<int> HideAssetsFromListAsync(
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        return _repository.HideAssetsFromListAsync(
            assetIds,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public Task<AssetStatistics> GetLocalAssetStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.GetLocalAssetStatisticsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AssetDirectorySummary>> ListAssetDirectoriesAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.ListAssetDirectoriesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListAssetExtensionsAsync(
        AssetFileTypeFilter fileType = AssetFileTypeFilter.All,
        CancellationToken cancellationToken = default)
    {
        return _repository.ListAssetExtensionsAsync(fileType, cancellationToken);
    }

    public Task<IReadOnlyList<ExactDuplicateGroup>> ListExactDuplicateGroupsAsync(
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return _repository.ListExactDuplicateGroupsAsync(limit, cancellationToken);
    }

    public Task<IReadOnlyList<ScanRoot>> ListScanRootsAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.ListScanRootsAsync(
            includeRemoved: false,
            cancellationToken);
    }

    public Task<ScanBatchSummary> ScanConfiguredRootsAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ScanRootsCoreAsync(
            includedRootIds: null,
            progress,
            cancellationToken);
    }

    public Task<ScanBatchSummary> ScanRootsAsync(
        IReadOnlyCollection<Guid> scanRootIds,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanRootIds);
        return ScanRootsCoreAsync(
            scanRootIds.ToHashSet(),
            progress,
            cancellationToken);
    }

    private async Task<ScanBatchSummary> ScanRootsCoreAsync(
        IReadOnlySet<Guid>? includedRootIds,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var roots = (await _repository.ListScanRootsAsync(
                includeRemoved: false,
                cancellationToken))
            .Where(root =>
                root.Enabled &&
                (includedRootIds is null || includedRootIds.Contains(root.Id)))
            .ToArray();
        var rootsScanned = 0;
        var rootsUnavailable = 0;
        var rootsFailed = 0;
        var filesDiscovered = 0;
        var filesIndexed = 0;
        var errors = 0;
        var cancelled = false;

        foreach (var root in roots)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            if (root.Status == ScanRootStatus.Offline)
            {
                rootsUnavailable++;
                progress?.Report(new ScanProgress(
                    ScanStage.Failed,
                    filesDiscovered,
                    filesIndexed,
                    errors,
                    root.Path,
                    "目录所在设备当前离线，已跳过。"));
                continue;
            }

            if (!Directory.Exists(root.Path))
            {
                rootsUnavailable++;
                errors++;
                await _repository.SetScanRootStatusAsync(
                    root.Id,
                    ScanRootStatus.Unavailable,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                progress?.Report(new ScanProgress(
                    ScanStage.Failed,
                    filesDiscovered,
                    filesIndexed,
                    errors,
                    root.Path,
                    "目录当前不可用，已跳过。"));
                continue;
            }

            try
            {
                var summary = await ScanDirectoryAsync(
                    root.Path,
                    progress,
                    cancellationToken,
                    root.Mode,
                    root.CreateFileFilter());
                rootsScanned++;
                filesDiscovered += summary.FilesDiscovered;
                filesIndexed += summary.FilesIndexed;
                errors += summary.Errors;
                if (summary.Status == ScanJobStatus.Cancelled)
                {
                    cancelled = true;
                    break;
                }
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                rootsFailed++;
                errors++;
                await _repository.SetScanRootStatusAsync(
                    root.Id,
                    ScanRootStatus.Error,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                progress?.Report(new ScanProgress(
                    ScanStage.Failed,
                    filesDiscovered,
                    filesIndexed,
                    errors,
                    root.Path,
                    exception.Message));
            }
        }

        return new ScanBatchSummary(
            roots.Length,
            rootsScanned,
            rootsUnavailable,
            rootsFailed,
            filesDiscovered,
            filesIndexed,
            errors,
            cancelled);
    }
    public async Task<ScanSummary> ScanDirectoryAsync(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ScanRootMode mode = ScanRootMode.Readonly,
        ScanFileFilter? fileFilter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var normalizedRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Scan root does not exist: {normalizedRoot}");
        }

        var startedAt = DateTimeOffset.UtcNow;
        progress?.Report(new ScanProgress(ScanStage.Initializing, 0, 0, 0, normalizedRoot));

        var scanRoot = await _repository.GetOrCreateScanRootAsync(
            normalizedRoot,
            mode,
            startedAt,
            cancellationToken);
        var effectiveFileFilter = fileFilter ?? scanRoot.CreateFileFilter();
        var excludedDirectoryPaths =
            await _repository.ListExcludedAssetDirectoryPathsAsync(cancellationToken);
        var deviceId = await _repository.GetOrCreateDeviceIdAsync(cancellationToken);
        var job = new ScanJob(
            Guid.NewGuid(),
            scanRoot.Id,
            ScanJobStatus.Running,
            startedAt,
            null,
            0,
            0,
            0,
            null);

        await _repository.CreateScanJobAsync(job, cancellationToken);

        var buffer = new List<DiscoveredFile>(BatchSize);
        var discovered = 0;
        var indexed = 0;
        var errors = 0;

        void Report(ScanStage stage, string? path, string? message = null)
        {
            progress?.Report(new ScanProgress(
                stage,
                discovered,
                indexed,
                errors,
                path,
                message));
        }

        async Task RegisterAsync(
            IReadOnlyCollection<DiscoveredFile> files,
            CancellationToken token)
        {
            var registered = await _repository.RegisterLocalFilesAsync(
                deviceId,
                files,
                DateTimeOffset.UtcNow,
                token);
            indexed += registered.Count;
        }

        async Task FlushAsync(CancellationToken token)
        {
            if (buffer.Count == 0)
            {
                return;
            }

            var currentBatch = buffer.ToArray();
            buffer.Clear();

            try
            {
                await RegisterAsync(currentBatch, token);
            }
            catch (Exception batchException)
                when (currentBatch.Length > 1 && batchException is not OperationCanceledException)
            {
                foreach (var file in currentBatch)
                {
                    try
                    {
                        await RegisterAsync([file], token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        errors++;
                        Report(ScanStage.Indexing, file.FullPath, exception.Message);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors++;
                Report(ScanStage.Indexing, currentBatch[0].FullPath, exception.Message);
            }
        }

        try
        {
            await _scanner.ScanAsync(
                normalizedRoot,
                excludedDirectoryPaths,
                async (file, token) =>
                {
                    if (!effectiveFileFilter.Matches(file.Extension, file.MimeType))
                    {
                        return;
                    }

                    discovered++;
                    buffer.Add(file);

                    if (buffer.Count >= BatchSize)
                    {
                        Report(ScanStage.Indexing, file.FullPath);
                        await FlushAsync(token);
                    }
                    else if (discovered % 25 == 0)
                    {
                        Report(ScanStage.Discovering, file.FullPath);
                    }
                },
                (error, _) =>
                {
                    errors++;
                    Report(ScanStage.Discovering, error.Path, error.Message);
                    return ValueTask.CompletedTask;
                },
                cancellationToken);

            await FlushAsync(cancellationToken);
            if (errors == 0)
            {
                await _repository.MarkMissingLocalLocationsAsync(
                    deviceId,
                    normalizedRoot,
                    startedAt,
                    effectiveFileFilter,
                    cancellationToken);
            }

            var completedAt = DateTimeOffset.UtcNow;
            job = job with
            {
                Status = ScanJobStatus.Completed,
                FinishedAt = completedAt,
                FilesDiscovered = discovered,
                FilesProcessed = indexed,
                Errors = errors
            };

            await _repository.UpdateScanJobAsync(job, cancellationToken);
            await _repository.MarkScanRootCompletedAsync(scanRoot.Id, completedAt, cancellationToken);
            if (errors > 0)
            {
                await _repository.SetScanRootStatusAsync(
                    scanRoot.Id,
                    ScanRootStatus.Error,
                    completedAt,
                    cancellationToken);
            }

            Report(ScanStage.Completed, normalizedRoot);

            return new ScanSummary(
                job.Id,
                job.Status,
                discovered,
                indexed,
                errors);
        }
        catch (OperationCanceledException)
        {
            job = job with
            {
                Status = ScanJobStatus.Cancelled,
                FinishedAt = DateTimeOffset.UtcNow,
                FilesDiscovered = discovered,
                FilesProcessed = indexed,
                Errors = errors
            };
            await _repository.UpdateScanJobAsync(job, CancellationToken.None);
            Report(ScanStage.Cancelled, normalizedRoot);

            return new ScanSummary(
                job.Id,
                job.Status,
                discovered,
                indexed,
                errors);
        }
        catch (Exception exception)
        {
            job = job with
            {
                Status = ScanJobStatus.Failed,
                FinishedAt = DateTimeOffset.UtcNow,
                FilesDiscovered = discovered,
                FilesProcessed = indexed,
                Errors = errors + 1,
                ErrorMessage = exception.Message
            };
            await _repository.UpdateScanJobAsync(job, CancellationToken.None);
            await _repository.SetScanRootStatusAsync(
                scanRoot.Id,
                ScanRootStatus.Error,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            errors++;
            Report(ScanStage.Failed, normalizedRoot, exception.Message);
            throw;
        }
    }
}
