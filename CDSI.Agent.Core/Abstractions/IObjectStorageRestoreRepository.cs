using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Core.Abstractions;

public interface IObjectStorageRestoreRepository
{
    Task CreateRestoreJobAsync(
        ObjectStorageRestoreJob job,
        IReadOnlyCollection<ObjectStorageRestoreItem> items,
        CancellationToken cancellationToken = default);

    Task SaveRestoreItemAsync(
        ObjectStorageRestoreItem item,
        CancellationToken cancellationToken = default);

    Task UpdateRestoreJobAsync(
        ObjectStorageRestoreJob job,
        CancellationToken cancellationToken = default);

    Task<ObjectStorageRestoreAudit?> GetRestoreJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
}
