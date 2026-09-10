using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Application.Scanning;

public sealed class ScanRootManagementService
{
    private readonly IAssetRepository _repository;

    public ScanRootManagementService(IAssetRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<ScanRoot>> ListExternalAsync(
        CancellationToken cancellationToken = default)
    {
        return (await _repository.ListScanRootsAsync(
                includeRemoved: false,
                cancellationToken))
            .Where(root => root.Mode == ScanRootMode.Readonly)
            .ToArray();
    }

    public Task<IReadOnlyList<ScanRoot>> ListAllAsync(
        CancellationToken cancellationToken = default)
    {
        return _repository.ListScanRootsAsync(
            includeRemoved: false,
            cancellationToken);
    }

    public Task<ScanRootRegistrationResult> AddExternalAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return AddExternalAsync(
            path,
            ScanFileFilter.AllFileTypes,
            Array.Empty<string>(),
            cancellationToken);
    }

    public Task<ScanRootRegistrationResult> AddExternalAsync(
        string path,
        AssetFileTypeFilter fileTypeFilter,
        CancellationToken cancellationToken = default)
    {
        return AddExternalAsync(
            path,
            fileTypeFilter,
            Array.Empty<string>(),
            cancellationToken);
    }

    public async Task<ScanRootRegistrationResult> AddExternalAsync(
        string path,
        IReadOnlyCollection<AssetFileTypeFilter> fileTypeFilters,
        IReadOnlyCollection<string> extensionWhitelist,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileFilter = new ScanFileFilter(fileTypeFilters, extensionWhitelist);

        var normalizedPath = NormalizePath(path);
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException(
                $"扫描目录不存在或当前不可用: {normalizedPath}");
        }

        var roots = await _repository.ListScanRootsAsync(
            includeRemoved: false,
            cancellationToken);
        var exactRoot = roots.FirstOrDefault(root =>
            PathsEqual(root.Path, normalizedPath));
        if (exactRoot?.Mode == ScanRootMode.Managed)
        {
            throw new InvalidOperationException(
                "该目录属于 CDSI 受管工作区，不能改为外部只读目录。");
        }

        var warnings = roots
            .Where(root => !PathsEqual(root.Path, normalizedPath))
            .Where(root =>
                IsUnder(normalizedPath, root.Path) ||
                IsUnder(root.Path, normalizedPath))
            .Select(root => $"与已配置目录重叠: {root.Path}")
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        var scanRoot = await _repository.GetOrCreateScanRootAsync(
            normalizedPath,
            ScanRootMode.Readonly,
            now,
            cancellationToken);
        await _repository.RestoreAssetDirectoryAsync(
            normalizedPath,
            cancellationToken);
        await _repository.SetScanRootFileFilterAsync(
            scanRoot.Id,
            fileFilter,
            now,
            cancellationToken);
        scanRoot = scanRoot with
        {
            FileTypeFilter = fileFilter.FileTypeFilter,
            ExtensionWhitelist = fileFilter.ExtensionWhitelist,
            FileTypeFilters = fileFilter.FileTypeFilters,
            UpdatedAt = now
        };
        var existingFilter = exactRoot?.CreateFileFilter();
        var requiresInitialScan = exactRoot is null ||
            !exactRoot.Enabled ||
            exactRoot.LastScannedAt is null ||
            !existingFilter!.HasSameConfiguration(fileFilter);
        return new ScanRootRegistrationResult(
            scanRoot,
            warnings,
            requiresInitialScan);
    }

    public Task<ScanRootRegistrationResult> AddExternalAsync(
        string path,
        AssetFileTypeFilter fileTypeFilter,
        IReadOnlyCollection<string> extensionWhitelist,
        CancellationToken cancellationToken = default)
    {
        var legacyFilter = new ScanFileFilter(fileTypeFilter, extensionWhitelist);
        return AddExternalAsync(
            path,
            legacyFilter.FileTypeFilters,
            legacyFilter.ExtensionWhitelist,
            cancellationToken);
    }

    public async Task SetEnabledAsync(
        Guid scanRootId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await EnsureExternalRootAsync(scanRootId, cancellationToken);
        await _repository.SetScanRootEnabledAsync(
            scanRootId,
            enabled,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public async Task SetFileTypeFilterAsync(
        Guid scanRootId,
        AssetFileTypeFilter fileTypeFilter,
        CancellationToken cancellationToken = default)
    {
        await SetFileFilterAsync(
            scanRootId,
            fileTypeFilter,
            Array.Empty<string>(),
            cancellationToken);
    }

    public async Task SetFileFilterAsync(
        Guid scanRootId,
        IReadOnlyCollection<AssetFileTypeFilter> fileTypeFilters,
        IReadOnlyCollection<string> extensionWhitelist,
        CancellationToken cancellationToken = default)
    {
        var fileFilter = new ScanFileFilter(fileTypeFilters, extensionWhitelist);

        await EnsureExternalRootAsync(scanRootId, cancellationToken);
        await _repository.SetScanRootFileFilterAsync(
            scanRootId,
            fileFilter,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public Task SetFileFilterAsync(
        Guid scanRootId,
        AssetFileTypeFilter fileTypeFilter,
        IReadOnlyCollection<string> extensionWhitelist,
        CancellationToken cancellationToken = default)
    {
        var legacyFilter = new ScanFileFilter(fileTypeFilter, extensionWhitelist);
        return SetFileFilterAsync(
            scanRootId,
            legacyFilter.FileTypeFilters,
            legacyFilter.ExtensionWhitelist,
            cancellationToken);
    }

    public async Task SetIdleScanScheduleAsync(
        Guid scanRootId,
        IdleScanSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        await EnsureExternalRootAsync(scanRootId, cancellationToken);
        await _repository.SetScanRootIdleScheduleAsync(
            scanRootId,
            schedule,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public async Task RemoveAsync(
        Guid scanRootId,
        CancellationToken cancellationToken = default)
    {
        await EnsureExternalRootAsync(scanRootId, cancellationToken);
        await _repository.RemoveScanRootAsync(
            scanRootId,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public Task<AssetDirectoryExclusionResult> ExcludeAssetDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _repository.ExcludeAssetDirectoryAsync(
            path,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private async Task EnsureExternalRootAsync(
        Guid scanRootId,
        CancellationToken cancellationToken)
    {
        var root = (await _repository.ListScanRootsAsync(
                includeRemoved: false,
                cancellationToken))
            .SingleOrDefault(item => item.Id == scanRootId)
            ?? throw new InvalidOperationException("扫描目录不存在。");
        if (root.Mode != ScanRootMode.Readonly)
        {
            throw new InvalidOperationException("受管工作目录不能通过扫描目录操作修改。");
        }
    }

    private static bool IsUnder(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != "." &&
            !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public sealed record ScanRootRegistrationResult(
    ScanRoot Root,
    IReadOnlyList<string> Warnings,
    bool RequiresInitialScan);
