using CDSI.Agent.Core.Git;

namespace CDSI.Agent.Core.Abstractions;

public interface IGitProfileRepository
{
    Task<IReadOnlyList<GitProfile>> ListGitProfilesAsync(
        CancellationToken cancellationToken = default);

    Task SaveGitProfileAsync(
        GitProfile profile,
        CancellationToken cancellationToken = default);

    Task SetDefaultGitProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    Task DeleteGitProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}
