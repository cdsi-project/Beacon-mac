using CDSI.Agent.Application.Assets;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Assets;

public sealed class AssetTagServiceTests
{
    [Fact]
    public async Task AssignRemoveAndFilterTags_PreservesLocalFiles()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var firstPath = Path.Combine(directory.Path, "first.md");
        var secondPath = Path.Combine(directory.Path, "second.mp4");
        await File.WriteAllTextAsync(firstPath, "first article");
        await File.WriteAllTextAsync(secondPath, "video fixture");
        var now = DateTimeOffset.UtcNow;
        var registered = await repository.RegisterLocalFilesAsync(
            deviceId,
            [
                CreateDiscoveredFile(firstPath, "text/markdown", now),
                CreateDiscoveredFile(secondPath, "video/mp4", now)
            ],
            now);
        var service = new AssetTagService(repository);

        Assert.Equal(1, await service.AssignAsync(
            "素材",
            [registered[0].AssetId]));
        Assert.Equal(1, await service.AssignAsync(
            "  素材  ",
            [registered[0].AssetId, registered[1].AssetId]));
        Assert.Equal(1, await service.AssignAsync(
            "待发布",
            [registered[0].AssetId]));

        var tags = await service.ListAsync();
        var materialTag = tags.Single(tag => tag.Name == "素材");
        var customTag = tags.Single(tag => tag.Name == "待发布");
        Assert.Equal(2, materialTag.AssetCount);
        Assert.Equal(1, customTag.AssetCount);
        Assert.Equal(2, await repository.GetAssetListCountAsync(
            new AssetListFilter(tagId: materialTag.Id)));
        Assert.Equal(
            ["first.md"],
            (await repository.ListAssetsAsync(
                new AssetListFilter(tagId: customTag.Id),
                100))
            .Select(asset => asset.OriginalFilename));

        var listed = await repository.ListAssetsAsync(100);
        Assert.Contains(
            listed.Single(asset => asset.AssetId == registered[0].AssetId).Tags,
            tag => tag == "素材");
        Assert.Contains(
            listed.Single(asset => asset.AssetId == registered[0].AssetId).Tags,
            tag => tag == "待发布");

        Assert.Equal(1, await service.RemoveAsync(
            materialTag.Id,
            [registered[0].AssetId]));
        Assert.Equal(
            registered[1].AssetId,
            Assert.Single(await repository.ListAssetsAsync(
                new AssetListFilter(tagId: materialTag.Id),
                100)).AssetId);
        Assert.Equal("first article", await File.ReadAllTextAsync(firstPath));
        Assert.Equal("video fixture", await File.ReadAllTextAsync(secondPath));

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task AssignAsync_RejectsInvalidNamesBeforeWriting()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var service = new AssetTagService(repository);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AssignAsync("   ", [Guid.NewGuid()]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AssignAsync("bad\nname", [Guid.NewGuid()]));
        Assert.Empty(await service.ListAsync());

        SqliteConnection.ClearAllPools();
    }

    private static DiscoveredFile CreateDiscoveredFile(
        string path,
        string mimeType,
        DateTimeOffset now)
    {
        var info = new FileInfo(path);
        return new DiscoveredFile(
            info.FullName,
            info.Name,
            info.Extension,
            mimeType,
            info.Length,
            now,
            now);
    }
}
