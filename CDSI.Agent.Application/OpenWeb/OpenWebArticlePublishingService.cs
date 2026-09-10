using System.Security.Cryptography;
using System.Text.Json;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.OpenWeb;

namespace CDSI.Agent.Application.OpenWeb;

public sealed class OpenWebArticlePublishingService
{
    private const int MaximumTitleLength = 200;
    private readonly OpenWebSettingsService _settingsService;
    private readonly IOpenWebPublicationRepository _publicationRepository;
    private readonly IOpenWebArticleContentReader _contentReader;
    private readonly IOpenWebArticlePublisher _publisher;

    public OpenWebArticlePublishingService(
        OpenWebSettingsService settingsService,
        IOpenWebPublicationRepository publicationRepository,
        IOpenWebArticleContentReader contentReader,
        IOpenWebArticlePublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(publicationRepository);
        ArgumentNullException.ThrowIfNull(contentReader);
        ArgumentNullException.ThrowIfNull(publisher);
        _settingsService = settingsService;
        _publicationRepository = publicationRepository;
        _contentReader = contentReader;
        _publisher = publisher;
    }

    public bool Supports(string path)
    {
        return _contentReader.Supports(path);
    }

    public async Task<OpenWebArticlePublishResult> PublishAsync(
        OpenWebArticlePublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AssetId == Guid.Empty)
        {
            throw new ArgumentException("资产 ID 无效。", nameof(request));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        var title = NormalizeTitle(request.Title);
        if (request.SourceId == Guid.Empty)
        {
            throw new ArgumentException("OpenWeb 源站 ID 无效。", nameof(request));
        }

        var connection = await _settingsService.GetConnectionAsync(
            request.SourceId,
            cancellationToken);
        var content = await _contentReader.ReadAsync(request.Path, cancellationToken);
        if (string.IsNullOrWhiteSpace(content.Html))
        {
            throw new InvalidOperationException("文章正文为空，无法发布。");
        }

        var existing = await _publicationRepository.GetOpenWebPublicationAsync(
            request.AssetId,
            _publisher.Publisher,
            connection.OriginDomain,
            cancellationToken);
        var payload = new OpenWebArticlePayload(
            title,
            content.Html,
            request.Status,
            content.Metadata?.Slug,
            content.Metadata?.Categories,
            content.Metadata?.Tags);
        var wasCreated = existing is null;
        OpenWebRemoteArticle remote;
        try
        {
            remote = await _publisher.PublishAsync(
                connection,
                payload,
                existing?.RemotePostId,
                cancellationToken);
        }
        catch (OpenWebRemoteArticleNotFoundException exception)
            when (existing is not null &&
                  exception.RemoteArticleId == existing.RemotePostId)
        {
            remote = await _publisher.PublishAsync(
                connection,
                payload,
                remotePostId: null,
                cancellationToken);
            wasCreated = true;
        }

        var synchronizedAt = DateTimeOffset.UtcNow;
        var publication = new OpenWebPublication(
            request.AssetId,
            _publisher.Publisher,
            connection.OriginDomain,
            remote.PostId,
            remote.Url,
            remote.Status,
            CreateContentSha256(payload),
            synchronizedAt);
        await _publicationRepository.SaveOpenWebPublicationAsync(
            publication,
            cancellationToken);
        return new OpenWebArticlePublishResult(
            publication,
            wasCreated);
    }

    private static string NormalizeTitle(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var title = string.Join(
            " ",
            value.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
        if (title.Length > MaximumTitleLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"文章标题最多允许 {MaximumTitleLength} 个字符。");
        }

        return title;
    }

    private static string CreateContentSha256(OpenWebArticlePayload article)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(article);
        return Convert.ToHexString(
                SHA256.HashData(canonical))
            .ToLowerInvariant();
    }
}

public sealed record OpenWebArticlePublishRequest(
    Guid AssetId,
    Guid SourceId,
    string Path,
    string Title,
    OpenWebArticleStatus Status);

public sealed record OpenWebArticlePublishResult(
    OpenWebPublication Publication,
    bool WasCreated);
