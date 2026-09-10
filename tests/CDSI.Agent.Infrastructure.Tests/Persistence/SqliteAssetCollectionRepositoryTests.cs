using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class SqliteAssetCollectionRepositoryTests
{
    [Fact]
    public async Task Version24SingleBackupBinding_MigratesToMultipleBindingTable()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var profile = new ObjectStorageProfile(
            Guid.NewGuid(),
            "旧版阿里云",
            ObjectStorageProvider.AliyunOss,
            "https://oss-cn-beijing.aliyuncs.com",
            "beacon-assets",
            "oss-cn-beijing",
            UseHttps: true,
            "access-key-id",
            now,
            now);
        await repository.SaveStorageProfileAsync(profile);
        var collection = new AssetCollection(
            Guid.NewGuid(),
            "旧版项目",
            AssetCollectionType.Mixed,
            now,
            now);
        Assert.True(await repository.CreateAssetCollectionAsync(collection));

        await using (var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM asset_collection_backup_profiles;
                UPDATE asset_collections
                SET backup_profile_id = $profile_id
                WHERE id = $collection_id;
                ALTER TABLE scan_roots DROP COLUMN idle_scan_unit;
                ALTER TABLE scan_roots DROP COLUMN idle_scan_interval;
                ALTER TABLE scan_roots DROP COLUMN idle_scan_enabled;
                DELETE FROM schema_migrations WHERE version >= 25;
                """;
            command.Parameters.AddWithValue("$profile_id", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("$collection_id", collection.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        await repository.InitializeAsync();

        var migrated = await repository.GetAssetCollectionAsync(collection.Id);
        Assert.Equal([profile.Id], migrated?.BackupProfileIds);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CollectionBackupBindings_PersistAndClearIndependently()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var firstProfile = new ObjectStorageProfile(
            Guid.NewGuid(),
            "腾讯归档",
            ObjectStorageProvider.TencentCos,
            "https://cos.ap-beijing.myqcloud.com",
            "beacon-assets",
            "ap-beijing",
            UseHttps: true,
            "secret-id",
            now,
            now);
        var secondProfile = new ObjectStorageProfile(
            Guid.NewGuid(),
            "七牛分发",
            ObjectStorageProvider.QiniuKodo,
            "https://s3-cn-east-1.qiniucs.com",
            "beacon-distribution",
            "cn-east-1",
            UseHttps: true,
            "access-key",
            now,
            now);
        await repository.SaveStorageProfileAsync(firstProfile);
        await repository.SaveStorageProfileAsync(secondProfile);
        var collection = new AssetCollection(
            Guid.NewGuid(),
            "项目 A",
            AssetCollectionType.Mixed,
            now,
            now)
        {
            BackupProfileIds = [firstProfile.Id, secondProfile.Id]
        };

        Assert.True(await repository.CreateAssetCollectionAsync(collection));

        var loaded = await repository.GetAssetCollectionAsync(collection.Id);
        var summary = Assert.Single(await repository.ListAssetCollectionsAsync());
        Assert.Equal(
            new[] { firstProfile.Id, secondProfile.Id }.Order().ToArray(),
            loaded?.BackupProfileIds.Order().ToArray());
        Assert.Equal(2, summary.BackupTargets.Count);
        Assert.Contains(summary.BackupTargets, target =>
            target.ProfileId == firstProfile.Id &&
            target.ProfileName == "腾讯归档" &&
            target.Provider == ObjectStorageProvider.TencentCos);
        Assert.Contains(summary.BackupTargets, target =>
            target.ProfileId == secondProfile.Id &&
            target.ProfileName == "七牛分发" &&
            target.Provider == ObjectStorageProvider.QiniuKodo);

        Assert.True(await repository.DeleteStorageProfileAsync(firstProfile.Id));

        loaded = await repository.GetAssetCollectionAsync(collection.Id);
        summary = Assert.Single(await repository.ListAssetCollectionsAsync());
        Assert.Equal([secondProfile.Id], loaded?.BackupProfileIds);
        var remainingTarget = Assert.Single(summary.BackupTargets);
        Assert.Equal(secondProfile.Id, remainingTarget.ProfileId);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CollectionMembership_IsIdempotentAndDoesNotDeleteAssets()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var first = CreateFile(Path.Combine(directory.Path, "first.mp4"), "first.mp4", 12);
        var second = CreateFile(Path.Combine(directory.Path, "cover.jpg"), "cover.jpg", 5);
        var registered = await repository.RegisterLocalFilesAsync(
            deviceId,
            [first, second],
            DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var collection = new AssetCollection(
            Guid.NewGuid(),
            "Episode 01",
            AssetCollectionType.Video,
            now,
            now);

        Assert.True(await repository.CreateAssetCollectionAsync(collection));
        Assert.False(await repository.CreateAssetCollectionAsync(
            collection with { Id = Guid.NewGuid(), Name = "episode 01" }));

        var assetIds = registered.Select(asset => asset.AssetId).ToArray();
        Assert.Equal(2, await repository.AddAssetsToCollectionAsync(
            collection.Id,
            assetIds,
            now.AddMinutes(1)));
        Assert.Equal(0, await repository.AddAssetsToCollectionAsync(
            collection.Id,
            assetIds,
            now.AddMinutes(2)));

        var summary = Assert.Single(await repository.ListAssetCollectionsAsync());
        var members = await repository.ListAssetCollectionMembersAsync(collection.Id);
        Assert.Equal(2, summary.AssetCount);
        Assert.Equal(17, summary.TotalSizeBytes);
        Assert.Equal(0, summary.BackedUpAssetCount);
        Assert.Equal(now, summary.CreatedAt);
        Assert.Equal(2, members.Count);
        Assert.All(members, member =>
            Assert.Equal(now.AddMinutes(1), member.AddedAt));
        Assert.Contains(members, member => member.Asset.OriginalFilename == "first.mp4");
        Assert.Contains(members, member => member.Asset.OriginalFilename == "cover.jpg");

        Assert.Equal(1, await repository.RemoveAssetsFromCollectionAsync(
            collection.Id,
            [assetIds[0]],
            now.AddMinutes(3)));
        Assert.Single(await repository.ListAssetCollectionMembersAsync(collection.Id));
        Assert.Equal(2, (await repository.ListAssetsAsync(100)).Count);

        SqliteConnection.ClearAllPools();
    }

    private static DiscoveredFile CreateFile(string path, string filename, long size)
    {
        return new DiscoveredFile(
            path,
            filename,
            Path.GetExtension(filename),
            filename.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                ? "video/mp4"
                : "image/jpeg",
            size,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }
}
