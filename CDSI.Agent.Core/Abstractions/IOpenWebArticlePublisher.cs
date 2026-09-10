using CDSI.Agent.Core.OpenWeb;

namespace CDSI.Agent.Core.Abstractions;

public interface IOpenWebArticlePublisher
{
    OpenWebPublisher Publisher { get; }

    Task<OpenWebRemoteArticle> PublishAsync(
        OpenWebConnection connection,
        OpenWebArticlePayload article,
        long? remotePostId,
        CancellationToken cancellationToken = default);
}
