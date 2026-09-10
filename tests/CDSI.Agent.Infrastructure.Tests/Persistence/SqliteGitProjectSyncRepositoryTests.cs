using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class SqliteGitProjectSyncRepositoryTests
{
    [Fact]
    public async Task SyncRecords_UpsertTheLatestStateAndRetainSnapshots()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var projectId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var first = CreateRecord(
            projectId,
            profileId,
            "旧项目名",
            "1111111111111111",
            DateTimeOffset.UtcNow.AddMinutes(-5));
        var latest = CreateRecord(
            projectId,
            profileId,
            "新项目名",
            "2222222222222222",
            DateTimeOffset.UtcNow);

        await repository.SaveGitProjectSyncAsync(first);
        await repository.SaveGitProjectSyncAsync(latest);

        var saved = Assert.Single(await repository.ListGitProjectSyncsAsync());
        Assert.Equal(projectId, saved.ProjectId);
        Assert.Equal(profileId, saved.ProfileId);
        Assert.Equal("新项目名", saved.ProjectName);
        Assert.Equal(latest.CommitId, saved.CommitId);
        Assert.Equal(latest.SyncedAt, saved.SyncedAt);
        Assert.Equal(2, saved.SyncedFiles);

        SqliteConnection.ClearAllPools();
    }

    private static GitProjectSyncRecord CreateRecord(
        Guid projectId,
        Guid profileId,
        string projectName,
        string commitId,
        DateTimeOffset syncedAt)
    {
        return new GitProjectSyncRecord(
            projectId,
            projectName,
            AssetCollectionType.Text,
            profileId,
            "文章仓库",
            GitHostingProvider.GitHub,
            "https://github.com/cdsi-project/articles.git",
            "main",
            commitId,
            SyncedFiles: 2,
            SyncedBytes: 128,
            CreatedCommit: true,
            syncedAt);
    }
}
