using CDSI.Agent.Application.Scanning;
using CDSI.Agent.Application.Workspaces;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Infrastructure.FileSystem;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Scanning;

public sealed class ScanConfigurationTests
{
    [Fact]
    public async Task ConfigureWorkspace_ChangesInboxWithoutMovingOrDeletingOldContent()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var service = new WorkspaceApplicationService(
            repository,
            new WorkspaceProvisioner());
        var firstPath = Path.Combine(directory.Path, "FirstWorkspace");
        var secondPath = Path.Combine(directory.Path, "SecondWorkspace");

        var first = await service.ConfigureAsync(firstPath);
        var markerPath = Path.Combine(first.Layout.AssetsPath, "keep.txt");
        await File.WriteAllTextAsync(markerPath, "preserve");
        var second = await service.ConfigureAsync(secondPath);
        var roots = await repository.ListScanRootsAsync(includeRemoved: true);
        var scanRootService = new ScanRootManagementService(repository);
        var managedModeError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scanRootService.AddExternalAsync(second.Layout.InboxPath));

        Assert.Equal(Path.GetFullPath(secondPath), second.Workspace.Path);
        Assert.Equal(Path.GetFullPath(firstPath), second.PreviousPath);
        Assert.Equal("preserve", await File.ReadAllTextAsync(markerPath));
        Assert.Contains("受管工作区", managedModeError.Message);
        Assert.Contains(
            roots,
            root =>
                root.Path == first.Layout.InboxPath &&
                root.Status == ScanRootStatus.Removed);
        Assert.Contains(
            roots,
            root =>
                root.Path == second.Layout.InboxPath &&
                root.Mode == ScanRootMode.Managed &&
                root.Enabled);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ExternalRootManagement_WarnsOnOverlapAndPreservesHistoryOnRemoval()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var service = new ScanRootManagementService(repository);
        var parent = Directory.CreateDirectory(Path.Combine(directory.Path, "Assets"));
        var child = Directory.CreateDirectory(Path.Combine(parent.FullName, "Video"));
        var filePath = Path.Combine(parent.FullName, "asset.txt");
        await File.WriteAllTextAsync(filePath, "asset");

        var parentResult = await service.AddExternalAsync(parent.FullName);
        var childResult = await service.AddExternalAsync(child.FullName);
        var scanService = new ScanApplicationService(new FileSystemScanner(), repository);
        await scanService.ScanDirectoryAsync(parent.FullName);
        var existingResult = await service.AddExternalAsync(parent.FullName);
        await service.SetEnabledAsync(parentResult.Root.Id, enabled: false);
        var disabled = Assert.Single(
            await service.ListExternalAsync(),
            root => root.Id == parentResult.Root.Id);
        await service.RemoveAsync(parentResult.Root.Id);
        var visible = await service.ListExternalAsync();
        var all = await repository.ListScanRootsAsync(includeRemoved: true);
        var assets = await scanService.ListAssetsAsync();

        Assert.Empty(parentResult.Warnings);
        Assert.True(parentResult.RequiresInitialScan);
        Assert.True(childResult.RequiresInitialScan);
        Assert.False(existingResult.RequiresInitialScan);
        Assert.Contains(parent.FullName, Assert.Single(childResult.Warnings));
        Assert.Equal(ScanRootStatus.Disabled, disabled.Status);
        Assert.DoesNotContain(visible, root => root.Id == parentResult.Root.Id);
        Assert.Equal(filePath, Assert.Single(assets).Path);
        Assert.Contains(
            all,
            root =>
                root.Id == parentResult.Root.Id &&
                root.Status == ScanRootStatus.Removed);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ScanRootsAsync_OnlyTraversesTheRequestedNewRoot()
    {
        using var directory = new TestDirectory();
        var existingDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, "Existing"));
        var newDirectory = Directory.CreateDirectory(
            Path.Combine(directory.Path, "New"));
        await File.WriteAllTextAsync(
            Path.Combine(existingDirectory.FullName, "existing.txt"),
            "existing");
        await File.WriteAllTextAsync(
            Path.Combine(newDirectory.FullName, "new.txt"),
            "new");

        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var rootService = new ScanRootManagementService(repository);
        var existingRoot = await rootService.AddExternalAsync(
            existingDirectory.FullName);
        var newRoot = await rootService.AddExternalAsync(newDirectory.FullName);
        var initialScanService = new ScanApplicationService(
            new FileSystemScanner(),
            repository);
        await initialScanService.ScanDirectoryAsync(existingDirectory.FullName);
        var existingLastScannedAt = (await rootService.ListExternalAsync())
            .Single(root => root.Id == existingRoot.Root.Id)
            .LastScannedAt;
        var recordingScanner = new RecordingScanner(new FileSystemScanner());
        var selectiveScanService = new ScanApplicationService(
            recordingScanner,
            repository);

        var summary = await selectiveScanService.ScanRootsAsync(
            [newRoot.Root.Id]);
        var rootsAfterScan = await rootService.ListExternalAsync();
        var assets = await selectiveScanService.ListAssetsAsync();

        Assert.Equal(1, summary.RootsConfigured);
        Assert.Equal(1, summary.RootsScanned);
        Assert.Equal(1, summary.FilesIndexed);
        Assert.Equal([newDirectory.FullName], recordingScanner.ScannedRoots);
        Assert.Equal(
            existingLastScannedAt,
            rootsAfterScan
                .Single(root => root.Id == existingRoot.Root.Id)
                .LastScannedAt);
        Assert.Contains(assets, asset => asset.OriginalFilename == "existing.txt");
        Assert.Contains(assets, asset => asset.OriginalFilename == "new.txt");

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ExcludeAssetDirectory_HidesExistingLocationsAndSkipsFutureScans()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var root = Directory.CreateDirectory(Path.Combine(directory.Path, "Assets"));
        var included = Directory.CreateDirectory(Path.Combine(root.FullName, "Included"));
        var excluded = Directory.CreateDirectory(Path.Combine(root.FullName, "Excluded"));
        var includedFile = Path.Combine(included.FullName, "keep.txt");
        var excludedFile = Path.Combine(excluded.FullName, "skip.txt");
        await File.WriteAllTextAsync(includedFile, "keep");
        await File.WriteAllTextAsync(excludedFile, "skip");
        var scanService = new ScanApplicationService(new FileSystemScanner(), repository);
        var rootService = new ScanRootManagementService(repository);

        await scanService.ScanDirectoryAsync(root.FullName);
        var excludedRoot = await rootService.AddExternalAsync(excluded.FullName);
        var exclusion = await rootService.ExcludeAssetDirectoryAsync(excluded.FullName);
        await File.WriteAllTextAsync(Path.Combine(excluded.FullName, "later.txt"), "later");
        var rescan = await scanService.ScanDirectoryAsync(root.FullName);
        var visibleAssets = await scanService.ListAssetsAsync();
        var visibleDirectories = await scanService.ListAssetDirectoriesAsync();
        var excludedPaths = await repository.ListExcludedAssetDirectoryPathsAsync();

        Assert.Equal(1, exclusion.ExcludedLocationCount);
        Assert.Equal(1, exclusion.StoppedScanRootCount);
        Assert.DoesNotContain(
            await rootService.ListExternalAsync(),
            scanRoot => scanRoot.Id == excludedRoot.Root.Id);
        Assert.Equal(1, rescan.FilesDiscovered);
        Assert.Equal("keep.txt", Assert.Single(visibleAssets).OriginalFilename);
        Assert.Equal(included.FullName, Assert.Single(visibleDirectories).Path);
        Assert.Equal(excluded.FullName, Assert.Single(excludedPaths));
        Assert.True(File.Exists(excludedFile));
        Assert.True(File.Exists(Path.Combine(excluded.FullName, "later.txt")));

        var restoredRoot = await rootService.AddExternalAsync(excluded.FullName);
        var restoredScan = await scanService.ScanRootsAsync([restoredRoot.Root.Id]);
        var restoredAssets = await scanService.ListAssetsAsync();
        Assert.Equal(2, restoredScan.FilesDiscovered);
        Assert.Empty(await repository.ListExcludedAssetDirectoryPathsAsync());
        Assert.Equal(
            ["keep.txt", "later.txt", "skip.txt"],
            restoredAssets
                .Select(asset => asset.OriginalFilename)
                .Order(StringComparer.Ordinal));

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ScanRootsAsync_IndexesOnlyTheConfiguredFileType()
    {
        using var directory = new TestDirectory();
        var root = Directory.CreateDirectory(Path.Combine(directory.Path, "Mixed"));
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "clip.mp4"), "video");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "notes.txt"), "notes");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "archive.zip"), "archive");

        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var rootService = new ScanRootManagementService(repository);
        var registration = await rootService.AddExternalAsync(
            root.FullName,
            AssetFileTypeFilter.Video);
        var scanService = new ScanApplicationService(
            new FileSystemScanner(),
            repository);

        var summary = await scanService.ScanRootsAsync([registration.Root.Id]);
        var asset = Assert.Single(await scanService.ListAssetsAsync());
        var savedRoot = Assert.Single(await rootService.ListExternalAsync());

        Assert.Equal(1, summary.FilesDiscovered);
        Assert.Equal(1, summary.FilesIndexed);
        Assert.Equal("clip.mp4", asset.OriginalFilename);
        Assert.Equal(AssetFileTypeFilter.Video, savedRoot.FileTypeFilter);
        Assert.Equal([AssetFileTypeFilter.Video], savedRoot.FileTypeFilters);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ScanRootsAsync_IndexesOnlyWhitelistedExtensions()
    {
        using var directory = new TestDirectory();
        var root = Directory.CreateDirectory(Path.Combine(directory.Path, "Mixed"));
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "clip.mp4"), "video");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "source.mov"), "source");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "notes.txt"), "notes");

        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var rootService = new ScanRootManagementService(repository);
        var registration = await rootService.AddExternalAsync(
            root.FullName,
            AssetFileTypeFilter.All,
            ["MOV"]);
        var scanService = new ScanApplicationService(
            new FileSystemScanner(),
            repository);

        var summary = await scanService.ScanRootsAsync([registration.Root.Id]);
        var asset = Assert.Single(await scanService.ListAssetsAsync());
        var savedRoot = Assert.Single(await rootService.ListExternalAsync());

        Assert.Equal(1, summary.FilesDiscovered);
        Assert.Equal(1, summary.FilesIndexed);
        Assert.Equal("source.mov", asset.OriginalFilename);
        Assert.Empty(savedRoot.CreateFileFilter().FileTypeFilters);
        Assert.Equal([".mov"], savedRoot.ExtensionWhitelist);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ScanRootsAsync_CombinesSelectedCategoriesAndExtensions()
    {
        using var directory = new TestDirectory();
        var root = Directory.CreateDirectory(Path.Combine(directory.Path, "Mixed"));
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "clip.mp4"), "video");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "cover.png"), "image");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "layout.psd"), "design");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "notes.txt"), "notes");

        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var rootService = new ScanRootManagementService(repository);
        var registration = await rootService.AddExternalAsync(
            root.FullName,
            [AssetFileTypeFilter.Video, AssetFileTypeFilter.Image],
            ["PSD"]);
        var scanService = new ScanApplicationService(
            new FileSystemScanner(),
            repository);

        var summary = await scanService.ScanRootsAsync([registration.Root.Id]);
        var assets = await scanService.ListAssetsAsync();
        var savedRoot = Assert.Single(await rootService.ListExternalAsync());

        Assert.Equal(3, summary.FilesDiscovered);
        Assert.Equal(3, summary.FilesIndexed);
        Assert.Equal(
            ["clip.mp4", "cover.png", "layout.psd"],
            assets.Select(asset => asset.OriginalFilename).Order().ToArray());
        Assert.Equal(
            [AssetFileTypeFilter.Video, AssetFileTypeFilter.Image],
            savedRoot.FileTypeFilters);
        Assert.Equal([".psd"], savedRoot.ExtensionWhitelist);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task PartialScan_DoesNotMarkUnseenLocationsMissing()
    {
        using var directory = new TestDirectory();
        var root = Directory.CreateDirectory(Path.Combine(directory.Path, "Assets"));
        var filePath = Path.Combine(root.FullName, "keep.txt");
        await File.WriteAllTextAsync(filePath, "keep");

        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "State", "cdsi.db"));
        var successfulService = new ScanApplicationService(
            new FileSystemScanner(),
            repository);
        await successfulService.InitializeAsync();
        await successfulService.ScanDirectoryAsync(root.FullName);

        File.Delete(filePath);
        var partialService = new ScanApplicationService(
            new ErrorOnlyScanner(),
            repository);
        var summary = await partialService.ScanDirectoryAsync(root.FullName);
        var asset = Assert.Single(await partialService.ListAssetsAsync());

        Assert.Equal(1, summary.Errors);
        Assert.Equal(AssetLocationStatus.Available, asset.LocationStatus);
        Assert.False(File.Exists(filePath));

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ScanConfiguredRoots_SkipsUnavailableRootAndContinues()
    {
        using var directory = new TestDirectory();
        var available = Directory.CreateDirectory(Path.Combine(directory.Path, "Available"));
        var unavailable = Directory.CreateDirectory(Path.Combine(directory.Path, "Unavailable"));
        var filePath = Path.Combine(available.FullName, "asset.txt");
        await File.WriteAllTextAsync(filePath, "asset");

        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "State", "cdsi.db"));
        var service = new ScanApplicationService(new FileSystemScanner(), repository);
        await service.InitializeAsync();
        await repository.GetOrCreateScanRootAsync(
            available.FullName,
            ScanRootMode.Readonly,
            DateTimeOffset.UtcNow);
        var unavailableRoot = await repository.GetOrCreateScanRootAsync(
            unavailable.FullName,
            ScanRootMode.Readonly,
            DateTimeOffset.UtcNow);
        Directory.Delete(unavailable.FullName);

        var summary = await service.ScanConfiguredRootsAsync();
        var assets = await service.ListAssetsAsync();
        var roots = await service.ListScanRootsAsync();

        Assert.Equal(2, summary.RootsConfigured);
        Assert.Equal(1, summary.RootsScanned);
        Assert.Equal(1, summary.RootsUnavailable);
        Assert.Equal(filePath, Assert.Single(assets).Path);
        Assert.Equal(
            ScanRootStatus.Unavailable,
            roots.Single(root => root.Id == unavailableRoot.Id).Status);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ScanConfiguredRoots_DoesNotTraverseAnOfflineVolume()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        const string mountPath = @"X:\";
        await repository.GetOrCreateScanRootAsync(
            Path.Combine(mountPath, "Creator"),
            ScanRootMode.Readonly,
            DateTimeOffset.UtcNow);
        await repository.ReconcileLocalVolumesAsync(
            [new LocalVolumeDescriptor(
                @"\\?\Volume{CDSI-OFFLINE-TEST}",
                "AABBCCDD",
                mountPath,
                "Offline test",
                "NTFS",
                "Removable")],
            DateTimeOffset.UtcNow);
        await repository.ReconcileLocalVolumesAsync(
            [],
            DateTimeOffset.UtcNow.AddSeconds(1));
        var offlineRoot = await repository.GetOrCreateScanRootAsync(
            Path.Combine(mountPath, "Creator"),
            ScanRootMode.Readonly,
            DateTimeOffset.UtcNow.AddSeconds(2));
        Assert.Equal(ScanRootStatus.Offline, offlineRoot.Status);
        await repository.SetScanRootEnabledAsync(
            offlineRoot.Id,
            enabled: false,
            DateTimeOffset.UtcNow.AddSeconds(3));
        await repository.SetScanRootEnabledAsync(
            offlineRoot.Id,
            enabled: true,
            DateTimeOffset.UtcNow.AddSeconds(4));
        var service = new ScanApplicationService(
            new UnexpectedScanner(),
            repository);

        var summary = await service.ScanConfiguredRootsAsync();
        var root = Assert.Single(await service.ListScanRootsAsync());

        Assert.Equal(1, summary.RootsConfigured);
        Assert.Equal(0, summary.RootsScanned);
        Assert.Equal(1, summary.RootsUnavailable);
        Assert.Equal(ScanRootStatus.Offline, root.Status);

        SqliteConnection.ClearAllPools();
    }

    private sealed class ErrorOnlyScanner : IFileScanner
    {
        public async Task ScanAsync(
            string rootPath,
            IReadOnlyCollection<string> excludedDirectoryPaths,
            Func<DiscoveredFile, CancellationToken, ValueTask> onFile,
            Func<ScanError, CancellationToken, ValueTask> onError,
            CancellationToken cancellationToken)
        {
            await onError(
                new ScanError(rootPath, "Expected partial traversal failure."),
                cancellationToken);
        }
    }

    private sealed class UnexpectedScanner : IFileScanner
    {
        public Task ScanAsync(
            string rootPath,
            IReadOnlyCollection<string> excludedDirectoryPaths,
            Func<DiscoveredFile, CancellationToken, ValueTask> onFile,
            Func<ScanError, CancellationToken, ValueTask> onError,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("离线卷不应调用文件扫描器。");
        }
    }

    private sealed class RecordingScanner : IFileScanner
    {
        private readonly IFileScanner _inner;

        public RecordingScanner(IFileScanner inner)
        {
            _inner = inner;
        }

        public List<string> ScannedRoots { get; } = [];

        public async Task ScanAsync(
            string rootPath,
            IReadOnlyCollection<string> excludedDirectoryPaths,
            Func<DiscoveredFile, CancellationToken, ValueTask> onFile,
            Func<ScanError, CancellationToken, ValueTask> onError,
            CancellationToken cancellationToken)
        {
            ScannedRoots.Add(rootPath);
            await _inner.ScanAsync(
                rootPath,
                excludedDirectoryPaths,
                onFile,
                onError,
                cancellationToken);
        }
    }
}
