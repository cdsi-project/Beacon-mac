using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class SqliteAssetRepositoryTests
{
    [Fact]
    public async Task RegisterLocalFilesAsync_IsIdempotentForTheSameDeviceAndPath()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var file = CreateFile(Path.Combine(directory.Path, "asset.txt"), "asset.txt");

        var first = await repository.RegisterLocalFilesAsync(
            deviceId,
            [file],
            DateTimeOffset.UtcNow);
        Assert.True(first[0].RequiresFingerprint);

        var saved = await repository.SaveSha256Async(
            first[0].AssetId,
            file.Size,
            file.ModifiedAt,
            new string('a', 64));
        var second = await repository.RegisterLocalFilesAsync(
            deviceId,
            [file],
            DateTimeOffset.UtcNow.AddSeconds(1));
        var assets = await repository.ListAssetsAsync(100);

        Assert.True(saved);
        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(first[0].AssetId, second[0].AssetId);
        Assert.False(second[0].RequiresFingerprint);
        Assert.Equal(new string('a', 64), Assert.Single(assets).Sha256);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ListAssetsAsync_AggregatesHealthyBackupProviderAndLatestTime()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var file = CreateFile(Path.Combine(directory.Path, "asset.txt"), "asset.txt");
        var registration = Assert.Single(await repository.RegisterLocalFilesAsync(
            deviceId,
            [file],
            DateTimeOffset.UtcNow));
        var firstBackupAt = DateTimeOffset.Parse("2026-08-20T01:00:00Z");
        var latestBackupAt = DateTimeOffset.Parse("2026-08-21T02:30:00Z");
        var ignoredUnhealthyAt = DateTimeOffset.Parse("2026-08-22T03:45:00Z");
        var firstProfile = CreateStorageProfile("第一 OSS", firstBackupAt);
        var secondProfile = CreateStorageProfile("第二 OSS", latestBackupAt);
        await repository.SaveStorageProfileAsync(firstProfile);
        await repository.SaveStorageProfileAsync(secondProfile);
        await repository.SaveObjectStorageLocationAsync(CreateStorageLocation(
            registration.AssetId,
            firstProfile.Id,
            "assets/first/asset.txt",
            StorageVerificationStatus.Healthy,
            firstBackupAt));
        await repository.SaveObjectStorageLocationAsync(CreateStorageLocation(
            registration.AssetId,
            secondProfile.Id,
            "assets/second/asset.txt",
            StorageVerificationStatus.Healthy,
            latestBackupAt));
        await repository.SaveObjectStorageLocationAsync(CreateStorageLocation(
            registration.AssetId,
            secondProfile.Id,
            "assets/unhealthy/asset.txt",
            StorageVerificationStatus.ChecksumMismatch,
            ignoredUnhealthyAt));

        var asset = Assert.Single(await repository.ListAssetsAsync(100));

        Assert.True(asset.HasHealthyObjectStorageBackup);
        Assert.Equal(["AliyunOss"], asset.HealthyBackupProviders);
        Assert.Equal(latestBackupAt, asset.LatestHealthyBackupAt);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MarkMissingLocalLocationsAsync_MarksOnlyLocationsNotSeenByTheScan()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var root = Path.Combine(directory.Path, "Assets");
        Directory.CreateDirectory(root);

        var scanStartedAt = DateTimeOffset.UtcNow;
        var missingFile = CreateFile(Path.Combine(root, "missing.txt"), "missing.txt");
        var availableFile = CreateFile(Path.Combine(root, "available.txt"), "available.txt");

        await repository.RegisterLocalFilesAsync(
            deviceId,
            [missingFile],
            scanStartedAt.AddSeconds(-1));
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [availableFile],
            scanStartedAt.AddSeconds(1));

        await repository.MarkMissingLocalLocationsAsync(deviceId, root, scanStartedAt);
        var assets = await repository.ListAssetsAsync(100);

        Assert.Equal(
            AssetLocationStatus.Missing,
            assets.Single(asset => asset.OriginalFilename == "missing.txt").LocationStatus);
        Assert.Equal(
            AssetLocationStatus.Available,
            assets.Single(asset => asset.OriginalFilename == "available.txt").LocationStatus);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MarkMissingLocalLocationsAsync_OnlyMarksTheScannedFileType()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var root = Directory.CreateDirectory(Path.Combine(directory.Path, "Assets"));
        var scanStartedAt = DateTimeOffset.UtcNow;
        var video = CreateFile(
            Path.Combine(root.FullName, "missing.mp4"),
            "missing.mp4") with
        {
            MimeType = "video/mp4"
        };
        var document = CreateFile(
            Path.Combine(root.FullName, "not-scanned.txt"),
            "not-scanned.txt");
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [video, document],
            scanStartedAt.AddSeconds(-1));

        await repository.MarkMissingLocalLocationsAsync(
            deviceId,
            root.FullName,
            scanStartedAt,
            new ScanFileFilter(AssetFileTypeFilter.Video));
        var assets = await repository.ListAssetsAsync(100);

        Assert.Equal(
            AssetLocationStatus.Missing,
            assets.Single(asset => asset.OriginalFilename == "missing.mp4").LocationStatus);
        Assert.Equal(
            AssetLocationStatus.Available,
            assets.Single(asset => asset.OriginalFilename == "not-scanned.txt").LocationStatus);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MarkMissingLocalLocationsAsync_OnlyMarksWhitelistedExtensions()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var root = Directory.CreateDirectory(Path.Combine(directory.Path, "Assets"));
        var scanStartedAt = DateTimeOffset.UtcNow;
        var mp4 = CreateFile(Path.Combine(root.FullName, "missing.mp4"), "missing.mp4");
        var mov = CreateFile(Path.Combine(root.FullName, "missing.mov"), "missing.mov");
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [mp4, mov],
            scanStartedAt.AddSeconds(-1));

        await repository.MarkMissingLocalLocationsAsync(
            deviceId,
            root.FullName,
            scanStartedAt,
            new ScanFileFilter(
                AssetFileTypeFilter.All,
                [".mp4"]));
        var assets = await repository.ListAssetsAsync(100);

        Assert.Equal(
            AssetLocationStatus.Missing,
            assets.Single(asset => asset.OriginalFilename == "missing.mp4").LocationStatus);
        Assert.Equal(
            AssetLocationStatus.Available,
            assets.Single(asset => asset.OriginalFilename == "missing.mov").LocationStatus);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ReconcileLocalVolumesAsync_RemapPathsWithoutScanningFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        const string originalMount = @"X:\";
        const string remappedMount = @"Y:\";
        var originalRoot = Path.Combine(originalMount, "Creator");
        var originalWorkspace = Path.Combine(originalMount, "Workspace");
        var originalFile = CreateFile(
            Path.Combine(originalRoot, "video.mp4"),
            "video.mp4") with
        {
            MimeType = "video/mp4"
        };
        var root = await repository.GetOrCreateScanRootAsync(
            originalRoot,
            ScanRootMode.Readonly,
            DateTimeOffset.UtcNow);
        await repository.SaveManagedWorkspaceAsync(
            deviceId,
            originalWorkspace,
            DateTimeOffset.UtcNow);
        var registered = await repository.RegisterLocalFilesAsync(
            deviceId,
            [originalFile],
            DateTimeOffset.UtcNow);
        var originalAssetId = Assert.Single(registered).AssetId;
        var originalVolume = CreateTestVolume(originalMount);

        var first = await repository.ReconcileLocalVolumesAsync(
            [originalVolume],
            DateTimeOffset.UtcNow);
        var addedAfterBinding = CreateFile(
            Path.Combine(originalRoot, "audio.mp3"),
            "audio.mp3") with
        {
            MimeType = "audio/mpeg"
        };
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [addedAfterBinding],
            DateTimeOffset.UtcNow);
        var remapped = await repository.ReconcileLocalVolumesAsync(
            [originalVolume with { MountPath = remappedMount }],
            DateTimeOffset.UtcNow.AddSeconds(1));

        var remappedRoot = Assert.Single(await repository.ListScanRootsAsync());
        var remappedAssets = await repository.ListAssetsAsync(100);
        var remappedWorkspace = await repository.GetManagedWorkspaceAsync(deviceId);

        Assert.Equal(1, first.NewlyTrackedVolumes);
        Assert.Equal(1, first.BoundScanRoots);
        Assert.Equal(1, first.BoundAssetLocations);
        Assert.Equal(root.Id, remappedRoot.Id);
        Assert.Equal(Path.Combine(remappedMount, "Creator"), remappedRoot.Path);
        Assert.Equal(1, remapped.RemappedScanRoots);
        Assert.Equal(2, remapped.RemappedAssetLocations);
        Assert.Equal(
            Path.Combine(remappedMount, "Workspace"),
            remappedWorkspace?.Path);
        Assert.Equal(
            originalAssetId,
            remappedAssets.Single(asset => asset.OriginalFilename == "video.mp4").AssetId);
        Assert.All(
            remappedAssets,
            asset => Assert.StartsWith(remappedMount, asset.Path));

        var disconnected = await repository.ReconcileLocalVolumesAsync(
            [],
            DateTimeOffset.UtcNow.AddSeconds(2));
        var offlineRoot = Assert.Single(await repository.ListScanRootsAsync());
        var offlineAssets = await repository.ListAssetsAsync(100);
        Assert.Equal(1, disconnected.OfflineVolumes);
        Assert.Equal(ScanRootStatus.Offline, offlineRoot.Status);
        Assert.All(
            offlineAssets,
            asset => Assert.Equal(AssetLocationStatus.Offline, asset.LocationStatus));

        var reconnected = await repository.ReconcileLocalVolumesAsync(
            [originalVolume with { MountPath = remappedMount }],
            DateTimeOffset.UtcNow.AddSeconds(3));
        var reconnectedRoot = Assert.Single(await repository.ListScanRootsAsync());
        var reconnectedAssets = await repository.ListAssetsAsync(100);
        Assert.Equal(1, reconnected.ReconnectedVolumes);
        Assert.Equal(ScanRootStatus.Active, reconnectedRoot.Status);
        Assert.All(
            reconnectedAssets,
            asset => Assert.Equal(
                AssetLocationStatus.Unverified,
                asset.LocationStatus));

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ListAssetsAsync_ReturnsStableDatabasePagesAndTotalCount()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var files = Enumerable.Range(1, 5)
            .Select(index => CreateFile(
                Path.Combine(directory.Path, $"asset-{index}.txt"),
                $"asset-{index}.txt"))
            .ToArray();
        var indexedAt = DateTimeOffset.Parse("2026-08-20T09:30:00+08:00");
        await repository.RegisterLocalFilesAsync(
            deviceId,
            files,
            indexedAt);

        var totalCount = await repository.GetAssetListCountAsync();
        var firstPage = await repository.ListAssetsAsync(2, 0);
        var secondPage = await repository.ListAssetsAsync(2, 2);
        var lastPage = await repository.ListAssetsAsync(2, 4);

        Assert.Equal(5, totalCount);
        Assert.Equal(
            ["asset-1.txt", "asset-2.txt"],
            firstPage.Select(asset => asset.OriginalFilename));
        Assert.Equal(
            ["asset-3.txt", "asset-4.txt"],
            secondPage.Select(asset => asset.OriginalFilename));
        Assert.Equal(
            ["asset-5.txt"],
            lastPage.Select(asset => asset.OriginalFilename));
        Assert.All(
            firstPage.Concat(secondPage).Concat(lastPage),
            asset => Assert.Equal(indexedAt, asset.DiscoveredAt));
        Assert.Empty(await repository.ListAssetsAsync(2, 6));

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task HideAssetsFromListAsync_PersistsAcrossRescansWithoutDeletingAssets()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var visible = CreateFile(
            Path.Combine(directory.Path, "visible.txt"),
            "visible.txt");
        var hidden = CreateFile(
            Path.Combine(directory.Path, "hidden.mp4"),
            "hidden.mp4") with
        {
            MimeType = "video/mp4"
        };
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [visible, hidden],
            DateTimeOffset.UtcNow);
        var hiddenAsset = (await repository.ListAssetsAsync(100))
            .Single(asset => asset.OriginalFilename == hidden.OriginalFilename);
        var hiddenAt = DateTimeOffset.UtcNow.AddMinutes(1);

        var affected = await repository.HideAssetsFromListAsync(
            [hiddenAsset.AssetId, hiddenAsset.AssetId],
            hiddenAt);
        var repeated = await repository.HideAssetsFromListAsync(
            [hiddenAsset.AssetId],
            hiddenAt.AddSeconds(1));
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [hidden],
            hiddenAt.AddMinutes(1));

        var listedAssets = await repository.ListAssetsAsync(100);
        var statistics = await repository.GetLocalAssetStatisticsAsync();
        var extensions = await repository.ListAssetExtensionsAsync();

        Assert.Equal(1, affected);
        Assert.Equal(0, repeated);
        Assert.Equal(1, await repository.GetAssetListCountAsync());
        Assert.Equal("visible.txt", Assert.Single(listedAssets).OriginalFilename);
        Assert.Equal(1, statistics.AssetCount);
        Assert.Equal(1, statistics.AvailableLocalFileCount);
        Assert.DoesNotContain(".mp4", extensions);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM assets a
            INNER JOIN asset_locations l ON l.asset_id = a.id
            WHERE a.id = $asset_id
              AND a.hidden_from_asset_list = 1
              AND a.hidden_from_asset_list_at = $hidden_at;
            """;
        command.Parameters.AddWithValue("$asset_id", hiddenAsset.AssetId.ToString("D"));
        command.Parameters.AddWithValue("$hidden_at", hiddenAt.ToString("O"));
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ListAssetsAsync_AppliesFileTypeAndCreationTimeFiltersInSqlite()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var januaryFirst = DateTimeOffset.Parse("2026-01-01T08:00:00+08:00");
        DiscoveredFile[] files =
        [
            CreateFile(
                Path.Combine(directory.Path, "filename-parent-only", "video.mp4"),
                "video.mp4") with
            {
                MimeType = "video/mp4",
                CreatedAt = januaryFirst
            },
            CreateFile(Path.Combine(directory.Path, "audio.mp3"), "audio.mp3") with
            {
                MimeType = "audio/mpeg",
                CreatedAt = januaryFirst.AddDays(1)
            },
            CreateFile(Path.Combine(directory.Path, "image.png"), "image.png") with
            {
                MimeType = "image/png",
                CreatedAt = januaryFirst.AddDays(2)
            },
            CreateFile(Path.Combine(directory.Path, "article.pdf"), "article.pdf") with
            {
                MimeType = "application/pdf",
                CreatedAt = januaryFirst.AddDays(3)
            },
            CreateFile(Path.Combine(directory.Path, "archive.zip"), "archive.zip") with
            {
                MimeType = "application/zip",
                CreatedAt = januaryFirst.AddDays(4)
            }
        ];
        await repository.RegisterLocalFilesAsync(
            deviceId,
            files,
            DateTimeOffset.UtcNow);
        var articleAsset = (await repository.ListAssetsAsync(100))
            .Single(asset => asset.OriginalFilename == "article.pdf");
        var projectB = new AssetCollection(
            Guid.NewGuid(),
            "Project B",
            AssetCollectionType.Text,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var projectA = projectB with
        {
            Id = Guid.NewGuid(),
            Name = "Project A"
        };
        Assert.True(await repository.CreateAssetCollectionAsync(projectB));
        Assert.True(await repository.CreateAssetCollectionAsync(projectA));
        Assert.Equal(
            1,
            await repository.AddAssetsToCollectionAsync(
                projectB.Id,
                [articleAsset.AssetId],
                DateTimeOffset.UtcNow));
        Assert.Equal(
            1,
            await repository.AddAssetsToCollectionAsync(
                projectA.Id,
                [articleAsset.AssetId],
                DateTimeOffset.UtcNow));

        var videoFilter = new AssetListFilter(AssetFileTypeFilter.Video);
        var documentFilter = new AssetListFilter(AssetFileTypeFilter.Document);
        var otherFilter = new AssetListFilter(AssetFileTypeFilter.Other);
        var dateFilter = new AssetListFilter(
            createdFrom: januaryFirst.AddDays(2),
            createdBefore: januaryFirst.AddDays(4));
        var combinedFilter = new AssetListFilter(
            AssetFileTypeFilter.Image,
            januaryFirst.AddDays(2),
            januaryFirst.AddDays(3));
        var extensionFilter = new AssetListFilter(
            AssetFileTypeFilter.Document,
            extension: "PDF");
        var filenameFilter = new AssetListFilter(
            filenameContains: "TICLE.P");
        var pathOnlyFilter = new AssetListFilter(
            filenameContains: "filename-parent-only");

        Assert.Equal(
            [".mp3", ".mp4", ".pdf", ".png", ".zip"],
            await repository.ListAssetExtensionsAsync());
        Assert.Equal(
            [".mp4"],
            await repository.ListAssetExtensionsAsync(AssetFileTypeFilter.Video));
        Assert.Equal(
            [".pdf"],
            await repository.ListAssetExtensionsAsync(AssetFileTypeFilter.Document));
        Assert.Equal(
            [".zip"],
            await repository.ListAssetExtensionsAsync(AssetFileTypeFilter.Other));

        Assert.Equal(
            ["video.mp4"],
            (await repository.ListAssetsAsync(videoFilter, 100))
                .Select(asset => asset.OriginalFilename));
        Assert.Equal(
            ["article.pdf"],
            (await repository.ListAssetsAsync(documentFilter, 100))
                .Select(asset => asset.OriginalFilename));
        Assert.Equal(
            ["archive.zip"],
            (await repository.ListAssetsAsync(otherFilter, 100))
                .Select(asset => asset.OriginalFilename));
        Assert.Equal(
            ["article.pdf", "image.png"],
            (await repository.ListAssetsAsync(dateFilter, 100))
                .Select(asset => asset.OriginalFilename)
                .Order());
        Assert.Equal(1, await repository.GetAssetListCountAsync(combinedFilter));
        Assert.Equal(
            "image.png",
            Assert.Single(await repository.ListAssetsAsync(combinedFilter, 100))
                .OriginalFilename);
        Assert.Equal(
            "article.pdf",
            Assert.Single(await repository.ListAssetsAsync(extensionFilter, 100))
                .OriginalFilename);
        Assert.Equal(1, await repository.GetAssetListCountAsync(filenameFilter));
        Assert.Equal(
            "article.pdf",
            Assert.Single(await repository.ListAssetsAsync(filenameFilter, 100))
                .OriginalFilename);
        Assert.Empty(await repository.ListAssetsAsync(pathOnlyFilter, 100));
        var assetsWithProjects = await repository.ListAssetsAsync(100);
        Assert.Equal(
            ["Project A", "Project B"],
            assetsWithProjects.Single(asset =>
                asset.OriginalFilename == "article.pdf").ProjectNames);
        Assert.Empty(assetsWithProjects.Single(asset =>
            asset.OriginalFilename == "video.mp4").ProjectNames);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ListAssetDirectoriesAsync_GroupsLocationsByTheirParentDirectory()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var root = Path.Combine(directory.Path, "Assets");
        var firstDirectory = Path.Combine(root, "Project A");
        var secondDirectory = Path.Combine(root, "Project B");
        var scanStartedAt = DateTimeOffset.UtcNow;
        var missing = CreateFile(
            Path.Combine(firstDirectory, "missing.txt"),
            "missing.txt") with
        {
            Size = 10
        };
        var available = CreateFile(
            Path.Combine(firstDirectory, "available.txt"),
            "available.txt") with
        {
            Size = 20
        };
        var other = CreateFile(
            Path.Combine(secondDirectory, "other.txt"),
            "other.txt") with
        {
            Size = 30
        };

        await repository.RegisterLocalFilesAsync(
            deviceId,
            [missing],
            scanStartedAt.AddSeconds(-1));
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [available, other],
            scanStartedAt.AddSeconds(1));
        await repository.MarkMissingLocalLocationsAsync(
            deviceId,
            root,
            scanStartedAt);

        var summaries = await repository.ListAssetDirectoriesAsync();

        Assert.Equal(2, summaries.Count);
        var first = summaries.Single(summary => summary.Path == firstDirectory);
        Assert.Equal(2, first.AssetCount);
        Assert.Equal(1, first.AvailableAssetCount);
        Assert.Equal(1, first.MissingAssetCount);
        Assert.Equal(20, first.AvailableSizeBytes);
        var second = summaries.Single(summary => summary.Path == secondDirectory);
        Assert.Equal(1, second.AssetCount);
        Assert.Equal(1, second.AvailableAssetCount);
        Assert.Equal(0, second.MissingAssetCount);
        Assert.Equal(30, second.AvailableSizeBytes);

        SqliteConnection.ClearAllPools();
    }


    [Fact]
    public async Task ListExactDuplicateGroupsAsync_GroupsOnlyMatchingSha256Values()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();

        var firstFile = CreateFile(Path.Combine(directory.Path, "first.txt"), "first.txt");
        var secondFile = CreateFile(Path.Combine(directory.Path, "second.txt"), "second.txt");
        var differentFile = CreateFile(Path.Combine(directory.Path, "different.txt"), "different.txt");
        var registered = await repository.RegisterLocalFilesAsync(
            deviceId,
            [firstFile, secondFile, differentFile],
            DateTimeOffset.UtcNow);

        await repository.SaveSha256Async(
            registered[0].AssetId,
            firstFile.Size,
            firstFile.ModifiedAt,
            new string('a', 64));
        await repository.SaveSha256Async(
            registered[1].AssetId,
            secondFile.Size,
            secondFile.ModifiedAt,
            new string('a', 64));
        await repository.SaveSha256Async(
            registered[2].AssetId,
            differentFile.Size,
            differentFile.ModifiedAt,
            new string('b', 64));

        var groups = await repository.ListExactDuplicateGroupsAsync(100);

        var group = Assert.Single(groups);
        Assert.Equal(new string('a', 64), group.Sha256);
        Assert.Equal(2, group.Assets.Count);
        Assert.Contains(group.Assets, asset => asset.OriginalFilename == "first.txt");
        Assert.Contains(group.Assets, asset => asset.OriginalFilename == "second.txt");

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SaveSha256Async_WhenMetadataChanged_DoesNotSaveAStaleHash()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var original = CreateFile(Path.Combine(directory.Path, "asset.txt"), "asset.txt");
        var registered = await repository.RegisterLocalFilesAsync(
            deviceId,
            [original],
            DateTimeOffset.UtcNow);
        var changed = original with
        {
            Size = original.Size + 1,
            ModifiedAt = original.ModifiedAt.AddSeconds(1)
        };
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [changed],
            DateTimeOffset.UtcNow.AddSeconds(1));

        var saved = await repository.SaveSha256Async(
            registered[0].AssetId,
            original.Size,
            original.ModifiedAt,
            new string('a', 64));
        var current = await repository.RegisterLocalFilesAsync(
            deviceId,
            [changed],
            DateTimeOffset.UtcNow.AddSeconds(2));

        Assert.False(saved);
        Assert.True(current[0].RequiresFingerprint);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task InitializeAsync_UpgradesAVersionOneDatabase()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var testConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();

        await using (var connection = new SqliteConnection(testConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                CREATE TABLE devices (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    platform TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE scan_roots (
                    id TEXT NOT NULL PRIMARY KEY,
                    path TEXT NOT NULL,
                    path_key TEXT NOT NULL UNIQUE,
                    enabled INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    last_scanned_at TEXT NULL
                );
                CREATE TABLE assets (
                    id TEXT NOT NULL PRIMARY KEY,
                    original_filename TEXT NOT NULL,
                    mime_type TEXT NULL,
                    extension TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    sha256 TEXT NULL,
                    created_at TEXT NOT NULL,
                    modified_at TEXT NOT NULL,
                    discovered_at TEXT NOT NULL,
                    status TEXT NOT NULL
                );
                CREATE TABLE asset_locations (
                    id TEXT NOT NULL PRIMARY KEY,
                    asset_id TEXT NOT NULL,
                    location_type TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    path TEXT NOT NULL,
                    path_key TEXT NOT NULL,
                    status TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL,
                    last_verified_at TEXT NULL,
                    FOREIGN KEY (asset_id) REFERENCES assets(id),
                    FOREIGN KEY (device_id) REFERENCES devices(id),
                    UNIQUE (device_id, path_key)
                );
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (1, $applied_at);
                """;
            command.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();

        await using (var connection = new SqliteConnection(testConnectionString))
        {
            await connection.OpenAsync();
            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync());

            await using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name IN (
                    'asset_metadata', 'managed_workspaces',
                    'storage_profiles', 'file_operations',
                    'file_operation_items', 'object_storage_locations',
                    'upload_jobs', 'upload_items',
                    'multipart_upload_sessions', 'asset_collections',
                    'asset_collection_items', 'agent_settings',
                    'openweb_publications', 'local_volumes', 'openweb_sources',
                    'restore_jobs', 'restore_items', 'asset_tags',
                    'asset_tag_links', 'asset_directory_exclusions',
                    'asset_collection_deletion_audit', 'git_profiles',
                    'git_project_syncs');
                """;
            var tableCount = Convert.ToInt32(await tableCommand.ExecuteScalarAsync());

            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'index' AND name IN (
                    'ix_asset_locations_type_asset_id',
                    'ix_assets_created_at_julian',
                    'ix_assets_mime_type',
                    'ix_assets_extension_lower',
                    'ix_scan_roots_volume_id',
                    'ix_asset_locations_volume_id',
                    'ix_asset_tag_links_tag_id',
                    'ix_asset_collection_deletion_audit_collection_id');
                """;
            var filterIndexCount = Convert.ToInt32(
                await indexCommand.ExecuteScalarAsync());

            await using var scanFilterColumnCommand = connection.CreateCommand();
            scanFilterColumnCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_table_info('scan_roots')
                WHERE (name = 'file_type_filter'
                  AND "notnull" = 1
                  AND dflt_value = '''All''')
                   OR (name = 'extension_whitelist_json'
                  AND "notnull" = 1
                  AND dflt_value = '''[]''')
                   OR (name = 'file_type_filters_json'
                  AND "notnull" = 1
                  AND dflt_value = '''["Video","Audio","Image","Document","Other"]''');
                """;
            var scanFilterColumnCount = Convert.ToInt32(
                await scanFilterColumnCommand.ExecuteScalarAsync());

            await using var assetVisibilityColumnCommand = connection.CreateCommand();
            assetVisibilityColumnCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_table_info('assets')
                WHERE (name = 'hidden_from_asset_list'
                  AND "notnull" = 1
                  AND dflt_value = '0')
                   OR (name = 'hidden_from_asset_list_at'
                  AND "notnull" = 0);
                """;
            var assetVisibilityColumnCount = Convert.ToInt32(
                await assetVisibilityColumnCommand.ExecuteScalarAsync());

            await using var locationVisibilityColumnCommand = connection.CreateCommand();
            locationVisibilityColumnCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM pragma_table_info('asset_locations')
                WHERE (name = 'excluded_from_asset_list'
                  AND "notnull" = 1
                  AND dflt_value = '0')
                   OR (name = 'excluded_from_asset_list_at'
                  AND "notnull" = 0);
                """;
            var locationVisibilityColumnCount = Convert.ToInt32(
                await locationVisibilityColumnCommand.ExecuteScalarAsync());

            await using var legacyTextTableCommand = connection.CreateCommand();
            legacyTextTableCommand.CommandText =
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM sqlite_master
                    WHERE type = 'table' AND name = 'asset_text');
                """;
            var legacyTextTableExists = Convert.ToInt32(
                await legacyTextTableCommand.ExecuteScalarAsync()) != 0;

            Assert.Equal(28, version);
            Assert.Equal(23, tableCount);
            Assert.Equal(8, filterIndexCount);
            Assert.Equal(3, scanFilterColumnCount);
            Assert.Equal(2, assetVisibilityColumnCount);
            Assert.Equal(2, locationVisibilityColumnCount);
            Assert.False(legacyTextTableExists);
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Version26Database_RemovesLegacyAssetTextTableAndItsRows()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var registered = await repository.RegisterLocalFilesAsync(
            deviceId,
            [CreateFile(Path.Combine(directory.Path, "legacy.md"), "legacy.md")],
            DateTimeOffset.UtcNow);
        var assetId = Assert.Single(registered).AssetId;

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE asset_text (
                    asset_id TEXT NOT NULL PRIMARY KEY,
                    plain_text TEXT NULL
                );
                INSERT INTO asset_text(asset_id, plain_text)
                VALUES($asset_id, 'legacy extracted body');
                ALTER TABLE scan_roots DROP COLUMN idle_scan_unit;
                ALTER TABLE scan_roots DROP COLUMN idle_scan_interval;
                ALTER TABLE scan_roots DROP COLUMN idle_scan_enabled;
                DELETE FROM schema_migrations WHERE version >= 27;
                """;
            command.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await repository.InitializeAsync();

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    (SELECT MAX(version) FROM schema_migrations),
                    EXISTS(
                        SELECT 1
                        FROM sqlite_master
                        WHERE type = 'table' AND name = 'asset_text'),
                    EXISTS(
                        SELECT 1
                        FROM assets
                        WHERE id = $asset_id);
                """;
            command.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(28, reader.GetInt32(0));
            Assert.False(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SaveMetadataAsync_CachesCurrentMetadataAndInvalidatesItAfterChange()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var original = CreateFile(Path.Combine(directory.Path, "photo.png"), "photo.png") with
        {
            MimeType = "image/png"
        };
        var registered = await repository.RegisterLocalFilesAsync(
            deviceId,
            [original],
            DateTimeOffset.UtcNow);

        var initialWork = await repository.GetMetadataWorkSummaryAsync(
            MetadataPipeline.CurrentVersion);
        var candidates = await repository.ListMetadataCandidatesAsync(
            MetadataPipeline.CurrentVersion,
            null,
            100);
        var metadata = new AssetMetadata(
            registered[0].AssetId,
            "test",
            MetadataPipeline.CurrentVersion,
            MetadataExtractionStatus.Extracted,
            original.Size,
            original.ModifiedAt,
            new AssetMetadataContent(AssetMediaKind.Image, Width: 1920, Height: 1080),
            DateTimeOffset.UtcNow,
            null);

        var saved = await repository.SaveMetadataAsync(metadata);
        var cachedWork = await repository.GetMetadataWorkSummaryAsync(
            MetadataPipeline.CurrentVersion);
        var loaded = await repository.GetMetadataAsync(registered[0].AssetId);
        var currentAssets = await repository.ListAssetsAsync(100);

        var changed = original with
        {
            Size = original.Size + 1,
            ModifiedAt = original.ModifiedAt.AddSeconds(1)
        };
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [changed],
            DateTimeOffset.UtcNow.AddSeconds(1));
        var invalidatedWork = await repository.GetMetadataWorkSummaryAsync(
            MetadataPipeline.CurrentVersion);
        var changedAssets = await repository.ListAssetsAsync(100);

        Assert.Equal(1, initialWork.Files);
        Assert.Single(candidates);
        Assert.True(saved);
        Assert.Equal(0, cachedWork.Files);
        Assert.Equal(1920, loaded?.Content?.Width);
        Assert.NotNull(Assert.Single(currentAssets).Metadata);
        Assert.Equal(1, invalidatedWork.Files);
        Assert.Null(Assert.Single(changedAssets).Metadata);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task GetLocalAssetStatisticsAsync_AggregatesAssetsStorageBackupsAndMedia()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var root = Path.Combine(directory.Path, "Assets");
        Directory.CreateDirectory(root);

        var scanStartedAt = DateTimeOffset.UtcNow;
        var video = CreateFile(Path.Combine(root, "video.mp4"), "video.mp4") with
        {
            MimeType = "video/mp4",
            Size = 100
        };
        var document = CreateFile(Path.Combine(root, "notes.txt"), "notes.txt") with
        {
            Size = 40
        };
        var audio = CreateFile(Path.Combine(root, "audio.wav"), "audio.wav") with
        {
            MimeType = "audio/wav",
            Size = 20
        };
        var image = CreateFile(Path.Combine(root, "cover.png"), "cover.png") with
        {
            MimeType = "image/png",
            Size = 30
        };
        var archive = CreateFile(Path.Combine(root, "source.zip"), "source.zip") with
        {
            MimeType = "application/zip",
            Size = 50
        };
        var missingVideo = CreateFile(
            Path.Combine(root, "missing.mp4"),
            "missing.mp4") with
        {
            MimeType = "video/mp4",
            Size = 25
        };

        var missingRegistration = await repository.RegisterLocalFilesAsync(
            deviceId,
            [missingVideo],
            scanStartedAt.AddSeconds(-1));
        var currentRegistrations = await repository.RegisterLocalFilesAsync(
            deviceId,
            [video, document, audio, image, archive],
            scanStartedAt.AddSeconds(1));

        await repository.SaveMetadataAsync(new AssetMetadata(
            currentRegistrations[0].AssetId,
            "test",
            MetadataPipeline.CurrentVersion,
            MetadataExtractionStatus.Extracted,
            video.Size,
            video.ModifiedAt,
            new AssetMetadataContent(
                AssetMediaKind.Video,
                DurationMilliseconds: 3_723_000),
            DateTimeOffset.UtcNow,
            null));

        var storageProfile = new ObjectStorageProfile(
            Guid.NewGuid(),
            "测试 OSS",
            ObjectStorageProvider.AliyunOss,
            "oss-cn-hangzhou.aliyuncs.com",
            "test-assets",
            "cn-hangzhou",
            true,
            "test-access-key-id",
            scanStartedAt,
            scanStartedAt);
        await repository.SaveStorageProfileAsync(storageProfile);
        await repository.SaveObjectStorageLocationAsync(new ObjectStorageLocation(
            Guid.NewGuid(),
            currentRegistrations[1].AssetId,
            storageProfile.Id,
            "assets/notes.txt",
            StorageVerificationStatus.Healthy,
            document.Size,
            null,
            "test-etag",
            scanStartedAt,
            scanStartedAt,
            scanStartedAt));
        await repository.SaveMetadataAsync(new AssetMetadata(
            missingRegistration[0].AssetId,
            "test",
            MetadataPipeline.CurrentVersion,
            MetadataExtractionStatus.Extracted,
            missingVideo.Size,
            missingVideo.ModifiedAt,
            new AssetMetadataContent(
                AssetMediaKind.Video,
                DurationMilliseconds: 60_000),
            DateTimeOffset.UtcNow,
            null));

        await repository.MarkMissingLocalLocationsAsync(
            deviceId,
            root,
            scanStartedAt);
        var statistics = await repository.GetLocalAssetStatisticsAsync();

        Assert.Equal(6, statistics.AssetCount);
        Assert.Equal(5, statistics.AvailableLocalFileCount);
        Assert.Equal(1, statistics.UnavailableAssetCount);
        Assert.Equal(240, statistics.TotalSizeBytes);
        Assert.Equal(2, statistics.VideoAssetCount);
        Assert.Equal(1, statistics.AudioAssetCount);
        Assert.Equal(1, statistics.ImageAssetCount);
        Assert.Equal(1, statistics.DocumentAssetCount);
        Assert.Equal(1, statistics.OtherAssetCount);
        Assert.Equal(1, statistics.BackedUpAssetCount);
        Assert.Equal(5, statistics.UnbackedUpAssetCount);
        Assert.Equal(3_783_000, statistics.VideoDurationMilliseconds);

        var changedVideo = video with
        {
            Size = 101,
            ModifiedAt = video.ModifiedAt.AddSeconds(1)
        };
        await repository.RegisterLocalFilesAsync(
            deviceId,
            [changedVideo],
            scanStartedAt.AddSeconds(2));
        var statisticsAfterChange = await repository.GetLocalAssetStatisticsAsync();

        Assert.Equal(6, statisticsAfterChange.AssetCount);
        Assert.Equal(5, statisticsAfterChange.AvailableLocalFileCount);
        Assert.Equal(241, statisticsAfterChange.TotalSizeBytes);
        Assert.Equal(2, statisticsAfterChange.VideoAssetCount);
        Assert.Equal(1, statisticsAfterChange.BackedUpAssetCount);
        Assert.Equal(60_000, statisticsAfterChange.VideoDurationMilliseconds);

        SqliteConnection.ClearAllPools();
    }

    private static ObjectStorageProfile CreateStorageProfile(
        string displayName,
        DateTimeOffset createdAt)
    {
        return new ObjectStorageProfile(
            Guid.NewGuid(),
            displayName,
            ObjectStorageProvider.AliyunOss,
            "oss-cn-hangzhou.aliyuncs.com",
            "test-assets",
            "cn-hangzhou",
            true,
            "test-access-key-id",
            createdAt,
            createdAt);
    }

    private static ObjectStorageLocation CreateStorageLocation(
        Guid assetId,
        Guid storageProfileId,
        string objectKey,
        StorageVerificationStatus status,
        DateTimeOffset verifiedAt)
    {
        return new ObjectStorageLocation(
            Guid.NewGuid(),
            assetId,
            storageProfileId,
            objectKey,
            status,
            5,
            null,
            "test-etag",
            verifiedAt,
            verifiedAt,
            verifiedAt);
    }

    private static DiscoveredFile CreateFile(string path, string filename)
    {
        return new DiscoveredFile(
            path,
            filename,
            Path.GetExtension(filename),
            "text/plain",
            5,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static LocalVolumeDescriptor CreateTestVolume(string mountPath)
    {
        return new LocalVolumeDescriptor(
            @"\\?\Volume{CDSI-TEST}",
            "1234ABCD",
            mountPath,
            "CDSI Test",
            "NTFS",
            "Removable");
    }
}
