using CDSI.Agent.Application.Scanning;
using CDSI.Agent.Application.Transfers;
using CDSI.Agent.Application.Workspaces;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Transfers;
using CDSI.Agent.Infrastructure.FileSystem;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Transfers;

public sealed class ManagedAssetTransferServiceTests
{
    [Fact]
    public async Task CopyAsync_PreservesSourceAndRegistersManagedLocationForSameAsset()
    {
        using var directory = new TestDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, "copy.bin");
        var service = new ManagedAssetTransferService(
            fixture.Repository,
            fixture.Provisioner,
            new VerifiedManagedFileTransfer());

        var result = await service.TransferAsync(
            [new ManagedAssetTransferRequest(fixture.Asset.AssetId, fixture.SourcePath)],
            ManagedAssetTransferAction.Copy);

        var item = Assert.Single(result.Items);
        Assert.Equal(FileOperationStatus.Completed, result.Status);
        Assert.Equal(FileOperationItemStatus.Completed, item.Status);
        Assert.False(item.SourceDeleted);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.NotNull(item.TargetPath);
        Assert.True(File.Exists(item.TargetPath));
        Assert.Equal(
            await File.ReadAllBytesAsync(fixture.SourcePath),
            await File.ReadAllBytesAsync(item.TargetPath!));

        var locations = (await fixture.ScanService.ListAssetsAsync())
            .Where(asset => asset.AssetId == fixture.Asset.AssetId)
            .ToArray();
        Assert.Equal(2, locations.Length);
        Assert.Contains(locations, asset =>
            asset.Path == fixture.SourcePath &&
            asset.LocationOwnership == AssetLocationOwnership.External);
        Assert.Contains(locations, asset =>
            asset.Path == item.TargetPath &&
            asset.LocationOwnership == AssetLocationOwnership.Managed);

        var audit = await fixture.Repository.GetFileOperationAsync(result.OperationId);
        Assert.Equal(FileOperationStatus.Completed, audit?.Operation.Status);
        Assert.Equal(FileOperationItemStatus.Completed, Assert.Single(audit!.Items).Status);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MoveAsync_DeletesSourceOnlyAfterVerifiedCopyAndMarksLocationMissing()
    {
        using var directory = new TestDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, "move.txt");
        var service = new ManagedAssetTransferService(
            fixture.Repository,
            fixture.Provisioner,
            new VerifiedManagedFileTransfer());

        var result = await service.TransferAsync(
            [new ManagedAssetTransferRequest(fixture.Asset.AssetId, fixture.SourcePath)],
            ManagedAssetTransferAction.Move);

        var item = Assert.Single(result.Items);
        Assert.Equal(FileOperationStatus.Completed, result.Status);
        Assert.True(item.SourceDeleted);
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(item.TargetPath));

        var locations = (await fixture.ScanService.ListAssetsAsync())
            .Where(asset => asset.AssetId == fixture.Asset.AssetId)
            .ToArray();
        Assert.Contains(locations, asset =>
            asset.Path == fixture.SourcePath &&
            asset.LocationStatus == AssetLocationStatus.Missing);
        Assert.Contains(locations, asset =>
            asset.Path == item.TargetPath &&
            asset.LocationStatus == AssetLocationStatus.Available);

        var audit = await fixture.Repository.GetFileOperationAsync(result.OperationId);
        Assert.True(Assert.Single(audit!.Items).SourceDeleted);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MoveAsync_WhenCopyFails_DoesNotDeleteSource()
    {
        using var directory = new TestDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, "keep-source.txt");
        var service = new ManagedAssetTransferService(
            fixture.Repository,
            fixture.Provisioner,
            new FailingCopyTransfer());

        var result = await service.TransferAsync(
            [new ManagedAssetTransferRequest(fixture.Asset.AssetId, fixture.SourcePath)],
            ManagedAssetTransferAction.Move);

        var item = Assert.Single(result.Items);
        Assert.Equal(FileOperationStatus.Failed, result.Status);
        Assert.Equal(FileOperationItemStatus.Failed, item.Status);
        Assert.False(item.SourceDeleted);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.False(File.Exists(item.TargetPath));

        var audit = await fixture.Repository.GetFileOperationAsync(result.OperationId);
        Assert.Equal(FileOperationStatus.Failed, audit?.Operation.Status);
        Assert.Contains("Expected copy failure", Assert.Single(audit!.Items).ErrorMessage);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MoveAsync_WhenDeleteFails_KeepsVerifiedCopyAndSource()
    {
        using var directory = new TestDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, "locked-source.txt");
        var service = new ManagedAssetTransferService(
            fixture.Repository,
            fixture.Provisioner,
            new FailingDeleteTransfer());

        var result = await service.TransferAsync(
            [new ManagedAssetTransferRequest(fixture.Asset.AssetId, fixture.SourcePath)],
            ManagedAssetTransferAction.Move);

        var item = Assert.Single(result.Items);
        Assert.Equal(FileOperationStatus.Failed, result.Status);
        Assert.False(item.SourceDeleted);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(item.TargetPath));
        Assert.Contains("工作目录副本已保留", item.ErrorMessage);

        var locations = (await fixture.ScanService.ListAssetsAsync())
            .Where(asset => asset.AssetId == fixture.Asset.AssetId)
            .ToArray();
        Assert.Contains(locations, asset =>
            asset.Path == item.TargetPath &&
            asset.LocationOwnership == AssetLocationOwnership.Managed);
        Assert.Contains(locations, asset =>
            asset.Path == fixture.SourcePath &&
            asset.LocationStatus == AssetLocationStatus.Available);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CopyAsync_WhenDifferentTargetExists_DoesNotOverwriteIt()
    {
        using var directory = new TestDirectory();
        var fixture = await CreateFixtureAsync(directory.Path, "collision.txt");
        var targetPath = Path.Combine(
            fixture.WorkspacePath,
            "Assets",
            fixture.Asset.AssetId.ToString("N"),
            Path.GetFileName(fixture.SourcePath));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, "existing target");
        var service = new ManagedAssetTransferService(
            fixture.Repository,
            fixture.Provisioner,
            new VerifiedManagedFileTransfer());

        var result = await service.TransferAsync(
            [new ManagedAssetTransferRequest(fixture.Asset.AssetId, fixture.SourcePath)],
            ManagedAssetTransferAction.Copy);

        Assert.Equal(FileOperationStatus.Failed, result.Status);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.Equal("existing target", await File.ReadAllTextAsync(targetPath));
        Assert.Contains("未覆盖现有文件", Assert.Single(result.Items).ErrorMessage);
        SqliteConnection.ClearAllPools();
    }

    private static async Task<TransferFixture> CreateFixtureAsync(
        string root,
        string filename)
    {
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "Source"));
        var sourcePath = Path.Combine(sourceDirectory.FullName, filename);
        await File.WriteAllBytesAsync(
            sourcePath,
            Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());

        var repository = new SqliteAssetRepository(
            Path.Combine(root, "State", "cdsi.db"));
        var scanService = new ScanApplicationService(
            new FileSystemScanner(),
            repository);
        await scanService.InitializeAsync();
        var provisioner = new WorkspaceProvisioner();
        var workspaceService = new WorkspaceApplicationService(
            repository,
            provisioner);
        var workspacePath = Path.Combine(root, "Workspace");
        await workspaceService.ConfigureAsync(workspacePath);
        await scanService.ScanDirectoryAsync(sourceDirectory.FullName);
        var asset = (await scanService.ListAssetsAsync())
            .Single(item => item.Path == sourcePath);
        return new TransferFixture(
            repository,
            scanService,
            provisioner,
            workspacePath,
            sourcePath,
            asset);
    }

    private sealed record TransferFixture(
        SqliteAssetRepository Repository,
        ScanApplicationService ScanService,
        WorkspaceProvisioner Provisioner,
        string WorkspacePath,
        string SourcePath,
        AssetListItem Asset);

    private sealed class FailingCopyTransfer : IManagedFileTransfer
    {
        public Task<VerifiedManagedFileCopy> CopyAndVerifyAsync(
            LocalAssetTransferSource source,
            string targetPath,
            Action<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new IOException("Expected copy failure.");
        }

        public Task DeleteSourceAsync(
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Delete must not be called.");
        }
    }

    private sealed class FailingDeleteTransfer : IManagedFileTransfer
    {
        private readonly VerifiedManagedFileTransfer _inner = new();

        public Task<VerifiedManagedFileCopy> CopyAndVerifyAsync(
            LocalAssetTransferSource source,
            string targetPath,
            Action<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.CopyAndVerifyAsync(
                source,
                targetPath,
                progress,
                cancellationToken);
        }

        public Task DeleteSourceAsync(
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            throw new IOException("Expected delete failure.");
        }
    }
}
