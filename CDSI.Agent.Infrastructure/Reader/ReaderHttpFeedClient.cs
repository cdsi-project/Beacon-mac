using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Reader;

namespace CDSI.Agent.Infrastructure.Reader;

public sealed partial class ReaderHttpFeedClient : IReaderFeedClient
{
    private const int MaximumRedirects = 5;
    private const int MaximumResponseBytes = 10 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly SyndicationFeedParser _parser;

    public ReaderHttpFeedClient(HttpClient httpClient, SyndicationFeedParser parser)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    public static HttpClient CreateHttpClient(string applicationVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
            UseCookies = false,
            Credentials = null,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CDSI-Beacon/{applicationVersion}");
        return client;
    }

    public async Task<ReaderFeedFetchResult> FetchAsync(
        ReaderFeedFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = ReaderUrl.ParseAndNormalize(request.FeedUri.AbsoluteUri);
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            await ValidateDestinationAsync(
                current,
                request.AllowPrivateNetwork,
                cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Get, current);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rss+xml"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/feed+json"));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.8));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml", 0.8));
            if (!string.IsNullOrWhiteSpace(request.ETag))
            {
                message.Headers.TryAddWithoutValidation("If-None-Match", request.ETag);
            }

            if (!string.IsNullOrWhiteSpace(request.LastModified))
            {
                message.Headers.TryAddWithoutValidation("If-Modified-Since", request.LastModified);
            }

            using var response = await _httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects)
                {
                    throw new HttpRequestException("Feed 重定向次数超过限制。");
                }

                var location = response.Headers.Location ??
                    throw new HttpRequestException("Feed 返回重定向但没有 Location。");
                current = ReaderUrl.ParseAndNormalize(
                    (location.IsAbsoluteUri ? location : new Uri(current, location)).AbsoluteUri);
                continue;
            }

            var etag = response.Headers.ETag?.ToString();
            var lastModified = response.Content.Headers.LastModified?.ToString("R");
            if (lastModified is null &&
                response.Headers.TryGetValues("Last-Modified", out var values))
            {
                lastModified = values.FirstOrDefault();
            }
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new ReaderFeedFetchResult(
                    current,
                    true,
                    (int)response.StatusCode,
                    etag ?? request.ETag,
                    lastModified ?? request.LastModified,
                    0,
                    null);
            }

            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimitedAsync(response.Content, cancellationToken);
            var content = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
            var parsed = ResolveRelativeLinks(
                _parser.Parse(content, response.Content.Headers.ContentType?.MediaType),
                current);
            return new ReaderFeedFetchResult(
                current,
                false,
                (int)response.StatusCode,
                etag,
                lastModified,
                bytes.Length,
                parsed);
        }

        throw new HttpRequestException("Feed 请求未完成。");
    }

    internal static async Task ValidateDestinationAsync(
        Uri uri,
        bool allowPrivateNetwork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ReaderUrl.ParseAndNormalize(uri.AbsoluteUri);
        if (allowPrivateNetwork)
        {
            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch (SocketException exception)
        {
            throw new HttpRequestException("无法解析 Feed 主机地址。", exception);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException("Feed 主机没有可用地址。");
        }

        if (addresses.Any(IsPrivateOrSpecialAddress))
        {
            throw new InvalidOperationException(
                "Feed 指向本机或私有网络。仅在确认来源可信时勾选允许局域网访问。");
        }
    }

    internal static bool IsPrivateOrSpecialAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast)
        {
            return true;
        }

        var bytes = address.MapToIPv6().GetAddressBytes();
        if (!address.IsIPv4MappedToIPv6 && (bytes[0] & 0xFE) == 0xFC)
        {
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            if (!address.IsIPv4MappedToIPv6)
            {
                return address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None);
            }

            address = address.MapToIPv4();
        }

        var ipv4 = address.GetAddressBytes();
        return ipv4[0] == 0 ||
            ipv4[0] == 10 ||
            ipv4[0] == 127 ||
            (ipv4[0] == 100 && ipv4[1] is >= 64 and <= 127) ||
            (ipv4[0] == 169 && ipv4[1] == 254) ||
            (ipv4[0] == 172 && ipv4[1] is >= 16 and <= 31) ||
            (ipv4[0] == 192 && ipv4[1] == 168) ||
            ipv4[0] >= 224;
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;
    }

    private static ReaderParsedFeed ResolveRelativeLinks(
        ReaderParsedFeed parsed,
        Uri baseUri)
    {
        return parsed with
        {
            SiteUrl = ResolveHttpUrl(parsed.SiteUrl, baseUri),
            Entries = parsed.Entries
                .Select(entry => entry with { Url = ResolveHttpUrl(entry.Url, baseUri) })
                .ToArray()
        };
    }

    private static string? ResolveHttpUrl(string? value, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(baseUri, value.Trim(), out var resolved) ||
            (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(resolved.UserInfo))
        {
            return null;
        }

        return ReaderUrl.ParseAndNormalize(resolved.AbsoluteUri).AbsoluteUri;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("Feed 响应超过 10 MB 限制。");
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("Feed 解压后的响应超过 10 MB 限制。");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static string Decode(byte[] bytes, string? declaredCharset)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encodingName = declaredCharset?.Trim('"', '\'', ' ');
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 256));
            encodingName = XmlEncodingRegex().Match(prefix) is { Success: true } match
                ? match.Groups[1].Value
                : null;
        }

        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(encodingName)
                ? new UTF8Encoding(false, true)
                : Encoding.GetEncoding(
                    encodingName,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException($"Feed 使用了不支持的字符编码: {encodingName}");
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(
                stream,
                encoding,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: false);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Feed 字符编码无效。", exception);
        }
    }

    [GeneratedRegex("encoding\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex XmlEncodingRegex();
}
