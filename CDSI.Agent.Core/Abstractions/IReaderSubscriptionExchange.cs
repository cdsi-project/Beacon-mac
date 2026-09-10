using CDSI.Agent.Core.Reader;

namespace CDSI.Agent.Core.Abstractions;

public interface IReaderSubscriptionExchange
{
    IReadOnlyList<ReaderSubscriptionDefinition> Parse(string opml);

    string Serialize(IReadOnlyCollection<ReaderFeed> feeds);
}
