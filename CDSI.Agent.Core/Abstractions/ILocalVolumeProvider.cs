using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Core.Abstractions;

public interface ILocalVolumeProvider
{
    Task<IReadOnlyList<LocalVolumeDescriptor>> ListMountedVolumesAsync(
        CancellationToken cancellationToken = default);
}
