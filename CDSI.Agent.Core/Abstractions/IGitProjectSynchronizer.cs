using CDSI.Agent.Core.Git;

namespace CDSI.Agent.Core.Abstractions;

public interface IGitProjectSynchronizer
{
    Task<GitProjectSyncResult> SyncAsync(
        GitProjectSyncRequest request,
        IProgress<GitProjectSyncProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
