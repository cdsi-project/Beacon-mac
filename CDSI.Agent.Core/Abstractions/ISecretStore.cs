namespace CDSI.Agent.Core.Abstractions;

public interface ISecretStore
{
    Task StoreAsync(
        string key,
        string secret,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task<string?> RetrieveAsync(
        string key,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(
        string key,
        CancellationToken cancellationToken = default);
}
