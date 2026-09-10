using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Application.Scanning;

public sealed class LocalVolumeReconciliationService
{
    private readonly ILocalVolumeProvider _volumeProvider;
    private readonly IAssetRepository _repository;
    private readonly SemaphoreSlim _reconciliationLock = new(1, 1);

    public LocalVolumeReconciliationService(
        ILocalVolumeProvider volumeProvider,
        IAssetRepository repository)
    {
        _volumeProvider = volumeProvider;
        _repository = repository;
    }

    public async Task<LocalVolumeReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        await _reconciliationLock.WaitAsync(cancellationToken);
        try
        {
            var mountedVolumes = await _volumeProvider.ListMountedVolumesAsync(
                cancellationToken);
            return await _repository.ReconcileLocalVolumesAsync(
                mountedVolumes,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        finally
        {
            _reconciliationLock.Release();
        }
    }
}
