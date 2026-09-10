using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class SqliteConfigurationRepositoryTests
{
    [Fact]
    public async Task SaveManagedWorkspaceAsync_UpdatesOneWorkspacePerDevice()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var firstAt = DateTimeOffset.UtcNow;
        var firstPath = Path.Combine(directory.Path, "First");
        var secondPath = Path.Combine(directory.Path, "Second");

        var first = await repository.SaveManagedWorkspaceAsync(
            deviceId,
            firstPath,
            firstAt);
        var second = await repository.SaveManagedWorkspaceAsync(
            deviceId,
            secondPath,
            firstAt.AddMinutes(1));
        var loaded = await repository.GetManagedWorkspaceAsync(deviceId);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(Path.GetFullPath(secondPath), loaded?.Path);
        Assert.Equal(firstAt.AddMinutes(1), loaded?.UpdatedAt);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ScanRootLifecycle_DisablesSoftRemovesAndReactivatesTheSameRoot()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var rootPath = Path.Combine(directory.Path, "Assets");
        var now = DateTimeOffset.UtcNow;

        var created = await repository.GetOrCreateScanRootAsync(
            rootPath,
            ScanRootMode.Readonly,
            now);
        Assert.Equal(AssetFileTypeFilter.All, created.FileTypeFilter);
        Assert.Equal(ScanFileFilter.AllFileTypes, created.FileTypeFilters);
        Assert.Equal(IdleScanSchedule.Disabled, created.GetIdleScanSchedule());
        await repository.MarkScanRootCompletedAsync(
            created.Id,
            now.AddSeconds(15));
        await repository.SetScanRootFileFilterAsync(
            created.Id,
            new ScanFileFilter(
                [AssetFileTypeFilter.Video, AssetFileTypeFilter.Image],
                ["PSD"]),
            now.AddSeconds(30));
        var changedFilter = Assert.Single(await repository.ListScanRootsAsync());
        var idleSchedule = new IdleScanSchedule(
            true,
            45,
            IdleScanIntervalUnit.Minutes);
        await repository.SetScanRootIdleScheduleAsync(
            created.Id,
            idleSchedule,
            now.AddSeconds(45));
        var scheduled = Assert.Single(await repository.ListScanRootsAsync());
        await repository.SetScanRootEnabledAsync(
            created.Id,
            enabled: false,
            now.AddMinutes(1));
        var disabled = Assert.Single(await repository.ListScanRootsAsync());
        await repository.RemoveScanRootAsync(created.Id, now.AddMinutes(2));
        var activeAfterRemoval = await repository.ListScanRootsAsync();
        var removed = Assert.Single(await repository.ListScanRootsAsync(includeRemoved: true));
        var reactivated = await repository.GetOrCreateScanRootAsync(
            rootPath,
            ScanRootMode.Readonly,
            now.AddMinutes(3));

        Assert.Equal(AssetFileTypeFilter.All, changedFilter.FileTypeFilter);
        Assert.Equal(
            [AssetFileTypeFilter.Video, AssetFileTypeFilter.Image],
            changedFilter.FileTypeFilters);
        Assert.Equal([".psd"], changedFilter.ExtensionWhitelist);
        Assert.Equal(idleSchedule, scheduled.GetIdleScanSchedule());
        Assert.Null(changedFilter.LastScannedAt);
        Assert.False(disabled.Enabled);
        Assert.Equal(ScanRootStatus.Disabled, disabled.Status);
        Assert.Empty(activeAfterRemoval);
        Assert.Equal(ScanRootStatus.Removed, removed.Status);
        Assert.NotNull(removed.RemovedAt);
        Assert.Equal(created.Id, reactivated.Id);
        Assert.True(reactivated.Enabled);
        Assert.Equal(ScanRootStatus.Active, reactivated.Status);
        Assert.Equal(
            [AssetFileTypeFilter.Video, AssetFileTypeFilter.Image],
            reactivated.FileTypeFilters);
        Assert.Equal([".psd"], reactivated.ExtensionWhitelist);
        Assert.Equal(idleSchedule, reactivated.GetIdleScanSchedule());
        Assert.Null(reactivated.RemovedAt);

        SqliteConnection.ClearAllPools();
    }
}
