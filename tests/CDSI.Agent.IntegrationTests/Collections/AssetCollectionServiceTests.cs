using CDSI.Agent.Application.Collections;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Collections;

public sealed class AssetCollectionServiceTests
{
    [Fact]
    public async Task Update_PreservesIdentityMembersAndBackupBindings()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var profile = new ObjectStorageProfile(
            Guid.NewGuid(),
            "阿里云主存储",
            ObjectStorageProvider.AliyunOss,
            "https://oss-cn-beijing.aliyuncs.com",
            "beacon-assets",
            "oss-cn-beijing",
            UseHttps: true,
            "access-key-id",
            now,
            now);
        await repository.SaveStorageProfileAsync(profile);
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var registered = Assert.Single(await repository.RegisterLocalFilesAsync(
            deviceId,
            [new DiscoveredFile(
                Path.Combine(directory.Path, "episode.mp4"),
                "episode.mp4",
                ".mp4",
                "video/mp4",
                128,
                now,
                now)],
            now));
        var service = new AssetCollectionService(repository);
        var project = await service.CreateAsync(
            "第一版名称",
            AssetCollectionType.Video,
            [profile.Id]);
        await service.AddAssetsAsync(project.Id, [registered.AssetId]);
        var otherProject = await service.CreateAsync(
            "其他项目",
            AssetCollectionType.Mixed);

        var updated = await service.UpdateAsync(
            project.Id,
            "  最终名称  ",
            AssetCollectionType.Mixed);

        var loaded = await repository.GetAssetCollectionAsync(project.Id);
        var summary = Assert.Single(
            await service.ListAsync(),
            item => item.Id == project.Id);
        Assert.Equal(project.Id, updated.Id);
        Assert.Equal(project.CreatedAt, updated.CreatedAt);
        Assert.Equal("最终名称", updated.Name);
        Assert.Equal(AssetCollectionType.Mixed, updated.Type);
        Assert.Equal([profile.Id], loaded?.BackupProfileIds);
        Assert.Equal("最终名称", loaded?.Name);
        Assert.Equal(AssetCollectionType.Mixed, summary.Type);
        Assert.Single(await service.GetMembersAsync(project.Id));

        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                otherProject.Id,
                "最终名称",
                AssetCollectionType.Text));
        Assert.Contains("已存在同名项目", conflict.Message);
        var unchanged = await repository.GetAssetCollectionAsync(otherProject.Id);
        Assert.Equal("其他项目", unchanged?.Name);
        Assert.Equal(AssetCollectionType.Mixed, unchanged?.Type);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CreateAddRemoveDeleteAndPrepareSync_PreservesLocalAsset()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var file = new DiscoveredFile(
            Path.Combine(directory.Path, "episode.mp4"),
            "episode.mp4",
            ".mp4",
            "video/mp4",
            128,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var registered = Assert.Single(await repository.RegisterLocalFilesAsync(
            deviceId,
            [file],
            DateTimeOffset.UtcNow));
        var service = new AssetCollectionService(repository);
        var backupProfileId = Guid.NewGuid();
        var secondaryBackupProfileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await repository.SaveStorageProfileAsync(new ObjectStorageProfile(
            backupProfileId,
            "阿里云主存储",
            ObjectStorageProvider.AliyunOss,
            "https://oss-cn-beijing.aliyuncs.com",
            "beacon-assets",
            "oss-cn-beijing",
            UseHttps: true,
            "access-key-id",
            now,
            now));
        await repository.SaveStorageProfileAsync(new ObjectStorageProfile(
            secondaryBackupProfileId,
            "腾讯云归档",
            ObjectStorageProvider.TencentCos,
            "https://cos.ap-beijing.myqcloud.com",
            "beacon-archive",
            "ap-beijing",
            UseHttps: true,
            "secret-id",
            now,
            now));

        var collection = await service.CreateAsync(
            "  Episode 01  ",
            AssetCollectionType.Video,
            [backupProfileId, secondaryBackupProfileId]);
        Assert.Equal("Episode 01", collection.Name);
        var expectedBackupProfileIds = new[]
        {
            backupProfileId,
            secondaryBackupProfileId
        };
        Assert.Equal(
            expectedBackupProfileIds.Order(),
            collection.BackupProfileIds.Order());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync("episode 01", AssetCollectionType.Mixed));
        var membershipError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareSelectedSyncAsync(collection.Id, [registered.AssetId]));
        Assert.Contains("只有项目内资产可以同步到 OSS", membershipError.Message);
        Assert.Equal(1, await service.AddAssetsAsync(
            collection.Id,
            [registered.AssetId, registered.AssetId]));

        var plan = await service.PrepareSyncAsync(collection.Id);
        Assert.Single(plan.Members);
        Assert.Single(plan.Assets);
        Assert.Equal(0, plan.UnavailableAssetCount);
        Assert.Equal(
            expectedBackupProfileIds.Order(),
            plan.Collection.BackupProfileIds.Order());
        var selectedPlan = await service.PrepareSelectedSyncAsync(
            collection.Id,
            [registered.AssetId, registered.AssetId]);
        Assert.Single(selectedPlan.Assets);
        Assert.Equal(collection.Id, selectedPlan.Collection.Id);

        Assert.Equal(1, await service.RemoveAssetsAsync(
            collection.Id,
            [registered.AssetId]));
        Assert.Empty(await service.GetMembersAsync(collection.Id));
        Assert.Equal(1, await service.AddAssetsAsync(
            collection.Id,
            [registered.AssetId]));

        var deleted = await service.DeleteAsync(collection.Id);

        Assert.Equal(collection.Id, deleted.Id);
        Assert.Equal(collection.Name, deleted.Name);
        Assert.Equal(collection.Type, deleted.Type);
        Assert.Empty(await service.ListAsync());
        Assert.Single(await repository.ListAssetsAsync(100));

        await using (var auditConnection = new SqliteConnection(
            $"Data Source={Path.Combine(directory.Path, "cdsi.db")};Pooling=False"))
        {
            await auditConnection.OpenAsync();
            await using (var auditCommand = auditConnection.CreateCommand())
            {
                auditCommand.CommandText =
                    """
                    SELECT collection_id, name, asset_count
                    FROM asset_collection_deletion_audit;
                    """;
                await using (var auditReader =
                    await auditCommand.ExecuteReaderAsync())
                {
                    Assert.True(await auditReader.ReadAsync());
                    Assert.Equal(collection.Id.ToString("D"), auditReader.GetString(0));
                    Assert.Equal("Episode 01", auditReader.GetString(1));
                    Assert.Equal(1, auditReader.GetInt32(2));
                    Assert.False(await auditReader.ReadAsync());
                }
            }

            await auditConnection.CloseAsync();
        }

        SqliteConnection.ClearAllPools();
    }
}
