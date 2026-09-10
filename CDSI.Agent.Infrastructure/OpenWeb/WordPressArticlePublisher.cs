using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.OpenWeb;

namespace CDSI.Agent.Infrastructure.OpenWeb;

public sealed class WordPressArticlePublisher : IOpenWebArticlePublisher
{
    private static readonly Regex ApiRootLinkPattern = new(
        "<(?<uri>[^>]+)>\\s*;[^,]*\\brel\\s*=\\s*[\\\"']?https://api\\.w\\.org/[\\\"']?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;

    public WordPressArticlePublisher(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public OpenWebPublisher Publisher => OpenWebPublisher.WordPress;

    public async Task<OpenWebRemoteArticle> PublishAsync(
        OpenWebConnection connection,
        OpenWebArticlePayload article,
        long? remotePostId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(article);
        if (remotePostId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remotePostId));
        }

        var siteRoot = new Uri($"https://{connection.OriginDomain}/", UriKind.Absolute);
        var apiRoot = await DiscoverApiRootAsync(siteRoot, cancellationToken);
        var categoryIds = article.Categories is null
            ? null
            : await ResolveTermIdsAsync(
                apiRoot,
                connection,
                "categories",
                "分类",
                article.Categories,
                cancellationToken);
        var tagIds = article.Tags is null
            ? null
            : await ResolveTermIdsAsync(
                apiRoot,
                connection,
                "tags",
                "标签",
                article.Tags,
                cancellationToken);
        var requestUri = BuildPostsUri(apiRoot, remotePostId);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = CreateAuthorizationHeader(connection);
        request.Content = JsonContent.Create(new WordPressPostRequest(
            article.Title,
            article.Html,
                article.Status == OpenWebArticleStatus.Published
                    ? "publish"
                    : "draft",
                article.Slug,
                categoryIds,
                tagIds));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var remoteError = await ReadErrorAsync(response, cancellationToken);
            if (remotePostId is not null &&
                response.StatusCode == HttpStatusCode.NotFound &&
                string.Equals(
                    remoteError.Code,
                    "rest_post_invalid_id",
                    StringComparison.Ordinal))
            {
                throw new OpenWebRemoteArticleNotFoundException(remotePostId.Value);
            }

            var message = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "WordPress 认证失败或当前账号没有文章发布权限。"
                : $"WordPress 发布失败（HTTP {(int)response.StatusCode}）。";
            if (!string.IsNullOrWhiteSpace(remoteError.Message))
            {
                message += $" {remoteError.Message}";
            }

            throw new InvalidOperationException(message);
        }

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        var remote = await JsonSerializer.DeserializeAsync<WordPressPostResponse>(
            responseStream,
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("WordPress 返回了空的文章结果。");
        if (remote.Id <= 0 ||
            !Uri.TryCreate(remote.Link, UriKind.Absolute, out var remoteUri) ||
            (remoteUri.Scheme != Uri.UriSchemeHttp &&
             remoteUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("WordPress 返回的文章 ID 或地址无效。");
        }

        var remoteStatus = string.Equals(
            remote.Status,
            "publish",
            StringComparison.Ordinal)
            ? OpenWebArticleStatus.Published
            : OpenWebArticleStatus.Draft;
        return new OpenWebRemoteArticle(remote.Id, remoteUri.AbsoluteUri, remoteStatus);
    }

    private async Task<Uri> DiscoverApiRootAsync(
        Uri siteRoot,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, siteRoot);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.IsSuccessStatusCode &&
            response.Headers.TryGetValues("Link", out var values))
        {
            foreach (var value in values)
            {
                var match = ApiRootLinkPattern.Match(value);
                if (match.Success &&
                    Uri.TryCreate(match.Groups["uri"].Value, UriKind.Absolute, out var discovered) &&
                    discovered.Scheme == Uri.UriSchemeHttps &&
                    string.Equals(
                        discovered.Host,
                        siteRoot.Host,
                        StringComparison.OrdinalIgnoreCase) &&
                    discovered.Port == siteRoot.Port)
                {
                    return discovered;
                }
            }
        }

        return new Uri(siteRoot, "wp-json/");
    }

    private static Uri BuildPostsUri(Uri apiRoot, long? remotePostId)
    {
        var route = remotePostId is null
            ? "wp/v2/posts"
            : $"wp/v2/posts/{remotePostId.Value}";
        return BuildApiUri(
            apiRoot,
            route,
            [("_fields", "id,link,status")]);
    }

    private async Task<long[]> ResolveTermIdsAsync(
        Uri apiRoot,
        OpenWebConnection connection,
        string route,
        string displayName,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var ids = new long[names.Count];
        for (var index = 0; index < names.Count; index++)
        {
            ids[index] = await ResolveTermIdAsync(
                apiRoot,
                connection,
                route,
                displayName,
                names[index],
                cancellationToken);
        }

        return ids;
    }

    private async Task<long> ResolveTermIdAsync(
        Uri apiRoot,
        OpenWebConnection connection,
        string route,
        string displayName,
        string name,
        CancellationToken cancellationToken)
    {
        var searchUri = BuildApiUri(
            apiRoot,
            $"wp/v2/{route}",
            [
                ("search", name),
                ("per_page", "100"),
                ("_fields", "id,name")
            ]);
        using (var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUri))
        {
            searchRequest.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            searchRequest.Headers.Authorization = CreateAuthorizationHeader(connection);
            using var searchResponse = await _httpClient.SendAsync(
                searchRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!searchResponse.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(searchResponse, cancellationToken);
                throw CreateTaxonomyException(
                    searchResponse.StatusCode,
                    displayName,
                    name,
                    "查询",
                    error);
            }

            await using var stream =
                await searchResponse.Content.ReadAsStreamAsync(cancellationToken);
            var terms = await JsonSerializer.DeserializeAsync<WordPressTermResponse[]>(
                stream,
                cancellationToken: cancellationToken) ?? [];
            var exact = terms.FirstOrDefault(term =>
                string.Equals(term.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null && exact.Id > 0)
            {
                return exact.Id;
            }
        }

        var createUri = BuildApiUri(apiRoot, $"wp/v2/{route}", []);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, createUri);
        createRequest.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        createRequest.Headers.Authorization = CreateAuthorizationHeader(connection);
        createRequest.Content = JsonContent.Create(new WordPressTermRequest(name));
        using var createResponse = await _httpClient.SendAsync(
            createRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(createResponse, cancellationToken);
            if (string.Equals(error.Code, "term_exists", StringComparison.Ordinal) &&
                error.TermId is > 0)
            {
                return error.TermId.Value;
            }

            throw CreateTaxonomyException(
                createResponse.StatusCode,
                displayName,
                name,
                "创建",
                error);
        }

        await using var responseStream =
            await createResponse.Content.ReadAsStreamAsync(cancellationToken);
        var created = await JsonSerializer.DeserializeAsync<WordPressTermResponse>(
            responseStream,
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                $"WordPress 返回了空的{displayName}结果。");
        if (created.Id <= 0)
        {
            throw new InvalidOperationException(
                $"WordPress 返回的{displayName} ID 无效。");
        }

        return created.Id;
    }

    private static Uri BuildApiUri(
        Uri apiRoot,
        string route,
        IReadOnlyList<(string Key, string Value)> query)
    {
        var encodedQuery = string.Join(
            "&",
            query.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        if (apiRoot.Query.Contains("rest_route=", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(apiRoot)
            {
                Query = $"rest_route=/{route}" +
                    (encodedQuery.Length == 0 ? string.Empty : $"&{encodedQuery}")
            };
            return builder.Uri;
        }

        var normalizedRoot = apiRoot.AbsoluteUri.EndsWith(
            "/",
            StringComparison.Ordinal)
            ? apiRoot
            : new Uri(apiRoot.AbsoluteUri + "/", UriKind.Absolute);
        return new Uri(
            normalizedRoot,
            route + (encodedQuery.Length == 0 ? string.Empty : $"?{encodedQuery}"));
    }

    private static InvalidOperationException CreateTaxonomyException(
        HttpStatusCode statusCode,
        string displayName,
        string name,
        string action,
        WordPressErrorResponse error)
    {
        var message =
            $"WordPress {displayName}“{name}”{action}失败（HTTP {(int)statusCode}）。";
        if (!string.IsNullOrWhiteSpace(error.Message))
        {
            message += $" {error.Message}";
        }

        return new InvalidOperationException(message);
    }

    private static AuthenticationHeaderValue CreateAuthorizationHeader(
        OpenWebConnection connection)
    {
        var credentialBytes = Encoding.UTF8.GetBytes(
            $"{connection.Username}:{connection.ApplicationPassword}");
        try
        {
            return new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(credentialBytes));
        }
        finally
        {
            Array.Clear(credentialBytes);
        }
    }

    private static async Task<WordPressErrorResponse> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[2_048];
        var length = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
        if (length == 0)
        {
            return new WordPressErrorResponse(null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(new string(buffer, 0, length));
            var code = document.RootElement.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString()
                : null;
            var message = document.RootElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            long? termId = null;
            if (document.RootElement.TryGetProperty("data", out var dataElement) &&
                dataElement.ValueKind == JsonValueKind.Object &&
                dataElement.TryGetProperty("term_id", out var termIdElement) &&
                termIdElement.TryGetInt64(out var parsedTermId))
            {
                termId = parsedTermId;
            }

            return new WordPressErrorResponse(code, message, termId);
        }
        catch (JsonException)
        {
            return new WordPressErrorResponse(null, null, null);
        }
    }

    private sealed record WordPressPostRequest(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("slug")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Slug,
        [property: JsonPropertyName("categories")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long[]? Categories,
        [property: JsonPropertyName("tags")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long[]? Tags);

    private sealed record WordPressTermRequest(
        [property: JsonPropertyName("name")] string Name);

    private sealed record WordPressTermResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record WordPressPostResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("status")] string Status);

    private sealed record WordPressErrorResponse(
        string? Code,
        string? Message,
        long? TermId);
}
