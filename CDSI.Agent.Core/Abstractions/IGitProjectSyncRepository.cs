using CDSI.Agent.Core.Git;

namespace CDSI.Agent.Core.Abstractions;

public interface IGitProjectSyncRepository
{
    Task<IReadOnlyList<GitProjectSyncRecord>> ListGitProjectSyncsAsync(
        CancellationToken cancellationToken = default);

    Task SaveGitProjectSyncAsync(
        GitProjectSyncRecord record,
        CancellationToken cancellationToken = default);
}
