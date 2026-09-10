using CDSI.Agent.Core.OpenWeb;

namespace CDSI.Agent.Core.Abstractions;

public interface IOpenWebSettingsRepository
{
    Task<IReadOnlyList<OpenWebSource>> ListOpenWebSourcesAsync(
        CancellationToken cancellationToken = default);

    Task SaveOpenWebSourceAsync(
        OpenWebSource source,
        CancellationToken cancellationToken = default);

    Task SetDefaultOpenWebSourceAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default);

    Task DeleteOpenWebSourceAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default);
}
