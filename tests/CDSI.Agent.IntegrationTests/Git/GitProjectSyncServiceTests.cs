using CDSI.Agent.Application.Collections;
using CDSI.Agent.Application.Git;
using CDSI.Agent.Application.Workspaces;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Infrastructure.FileSystem;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Git;

public sealed class GitProjectSyncServiceTests
{
    [Fact]
    public async Task PrepareAndSync_ResolveTheSelectedProjectRepositoryAndCredential()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var workspacePath = Path.Combine(directory.Path, "workspace");
        var workspaceService = new WorkspaceApplicationService(
            repository,
            new WorkspaceProvisioner());
        await workspaceService.ConfigureAsync(workspacePath);

        var assetPath = Path.Combine(directory.Path, "article.md");
        await File.WriteAllTextAsync(assetPath, "# Article");
        var info = new FileInfo(assetPath);
        var deviceId = await repository.GetOrCreateDeviceIdAsync();
        var registered = Assert.Single(await repository.RegisterLocalFilesAsync(
            deviceId,
            [new DiscoveredFile(
                info.FullName,
                info.Name,
                info.Extension,
                "text/markdown",
                info.Length,
                new DateTimeOffset(info.CreationTimeUtc),
                new DateTimeOffset(info.LastWriteTimeUtc))],
            DateTimeOffset.UtcNow));

        var collectionService = new AssetCollectionService(repository);
        var project = await collectionService.CreateAsync(
            "文章项目",
            AssetCollectionType.Text);
        await collectionService.AddAssetsAsync(project.Id, [registered.AssetId]);

        var gitProfileService = new GitProfileService(
            repository,
            new InMemorySecretStore());
        var configured = await gitProfileService.SaveAsync(new SaveGitProfileRequest(
            null,
            "仓库2",
            GitHostingProvider.GitHub,
            "https://github.com/cdsi-project/articles.git",
            "main",
            GitAuthenticationMethod.Password,
            "cdsi-project",
            "test-token",
            null,
            IsDefault: false));
        var synchronizer = new CapturingSynchronizer();
        var service = new GitProjectSyncService(
            collectionService,
            gitProfileService,
            workspaceService,
            synchronizer,
            repository);

        var preview = await service.PrepareAsync(project.Id, configured.Profile.Id);
        var result = await service.SyncAsync(project.Id, configured.Profile.Id);

        Assert.Equal(project.Id, preview.ProjectId);
        Assert.Equal("仓库2", preview.ProfileName);
        Assert.Equal("main", preview.Branch);
        Assert.Equal(1, preview.AssetCount);
        Assert.Equal(info.Length, preview.TotalBytes);
        var request = Assert.IsType<GitProjectSyncRequest>(synchronizer.Request);
        Assert.Equal(project.Id, request.Project.Id);
        Assert.Equal(configured.Profile.Id, request.Profile.Id);
        Assert.Equal("test-token", request.Password);
        Assert.Equal(Path.GetFullPath(workspacePath), request.WorkspacePath);
        Assert.Equal(registered.AssetId, Assert.Single(request.Assets).AssetId);
        Assert.Equal(configured.Profile.Id, result.ProfileId);
        var saved = Assert.Single(await service.ListAsync());
        Assert.Equal(project.Id, saved.ProjectId);
        Assert.Equal("文章项目", saved.ProjectName);
        Assert.Equal(configured.Profile.Id, saved.ProfileId);
        Assert.Equal("仓库2", saved.ProfileName);
        Assert.Equal("0123456789abcdef", saved.CommitId);
        Assert.Equal(1, saved.SyncedFiles);
        Assert.Equal(info.Length, saved.SyncedBytes);

        SqliteConnection.ClearAllPools();
    }

    private sealed class CapturingSynchronizer : IGitProjectSynchronizer
    {
        public GitProjectSyncRequest? Request { get; private set; }

        public Task<GitProjectSyncResult> SyncAsync(
            GitProjectSyncRequest request,
            IProgress<GitProjectSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new GitProjectSyncResult(
                request.Project.Id,
                request.Profile.Id,
                request.Profile.RepositoryUrl,
                request.Profile.DefaultBranch,
                "0123456789abcdef",
                request.Assets.Count,
                request.Assets.Sum(asset => asset.Size),
                CreatedCommit: true));
        }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = [];

        public Task StoreAsync(
            string key,
            string secret,
            CancellationToken cancellationToken = default)
        {
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_secrets.ContainsKey(key));
        }

        public Task<string?> RetrieveAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_secrets.GetValueOrDefault(key));
        }

        public Task DeleteAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            _secrets.Remove(key);
            return Task.CompletedTask;
        }
    }
}
