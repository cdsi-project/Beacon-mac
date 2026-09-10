namespace CDSI.Agent.Core.OpenWeb;

public sealed class OpenWebRemoteArticleNotFoundException : Exception
{
    public OpenWebRemoteArticleNotFoundException(long remoteArticleId)
        : base($"OpenWeb remote article {remoteArticleId} no longer exists.")
    {
        if (remoteArticleId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remoteArticleId));
        }

        RemoteArticleId = remoteArticleId;
    }

    public long RemoteArticleId { get; }
}
