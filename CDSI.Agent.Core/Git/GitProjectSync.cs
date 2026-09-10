using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;

namespace CDSI.Agent.Core.Git;

public static class GitProjectSyncConventions
{
    public const string ManifestFileName = ".cdsi-project.json";
}

public sealed record GitProjectSyncRequest(
    GitProfile Profile,
    string? Password,
    string WorkspacePath,
    AssetCollection Project,
    IReadOnlyList<AssetListItem> Assets);

public sealed record GitProjectSyncProgress(
    string Stage,
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string? CurrentPath = null);

public sealed record GitProjectSyncResult(
    Guid ProjectId,
    Guid ProfileId,
    string RepositoryUrl,
    string Branch,
    string CommitId,
    int SyncedFiles,
    long SyncedBytes,
    bool CreatedCommit);

public sealed record GitProjectSyncRecord(
    Guid ProjectId,
    string ProjectName,
    AssetCollectionType ProjectType,
    Guid ProfileId,
    string ProfileName,
    GitHostingProvider Provider,
    string RepositoryUrl,
    string Branch,
    string CommitId,
    int SyncedFiles,
    long SyncedBytes,
    bool CreatedCommit,
    DateTimeOffset SyncedAt);
