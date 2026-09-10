namespace CDSI.Agent.Core.OpenWeb;

public enum OpenWebPublisher
{
    WordPress
}

public enum OpenWebArticleStatus
{
    Draft,
    Published
}

public sealed record OpenWebPublication(
    Guid AssetId,
    OpenWebPublisher Publisher,
    string OriginDomain,
    long RemotePostId,
    string RemoteUrl,
    OpenWebArticleStatus Status,
    string ContentSha256,
    DateTimeOffset SynchronizedAt);

public sealed record OpenWebArticleMetadata(
    string? Slug,
    IReadOnlyList<string>? Categories,
    IReadOnlyList<string>? Tags);

public sealed record OpenWebArticleContent(
    string Html,
    OpenWebArticleMetadata? Metadata = null);

public sealed record OpenWebArticlePayload(
    string Title,
    string Html,
    OpenWebArticleStatus Status,
    string? Slug = null,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? Tags = null);

public sealed record OpenWebRemoteArticle(
    long PostId,
    string Url,
    OpenWebArticleStatus Status);
