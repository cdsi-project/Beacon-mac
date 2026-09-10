using CDSI.Agent.Core.Reader;

namespace CDSI.Agent.Core.Abstractions;

public interface IReaderFeedClient
{
    Task<ReaderFeedFetchResult> FetchAsync(
        ReaderFeedFetchRequest request,
        CancellationToken cancellationToken = default);
}
