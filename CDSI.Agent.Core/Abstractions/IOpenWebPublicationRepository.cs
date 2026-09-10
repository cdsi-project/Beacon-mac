using CDSI.Agent.Core.OpenWeb;

namespace CDSI.Agent.Core.Abstractions;

public interface IOpenWebPublicationRepository
{
    Task<OpenWebPublication?> GetOpenWebPublicationAsync(
        Guid assetId,
        OpenWebPublisher publisher,
        string originDomain,
        CancellationToken cancellationToken = default);

    Task SaveOpenWebPublicationAsync(
        OpenWebPublication publication,
        CancellationToken cancellationToken = default);
}
