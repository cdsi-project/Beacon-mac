using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Core.Abstractions;

public interface IStorageProfileRepository
{
    Task<IReadOnlyList<ObjectStorageProfile>> ListStorageProfilesAsync(
        CancellationToken cancellationToken = default);

    Task SaveStorageProfileAsync(
        ObjectStorageProfile profile,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteStorageProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}
