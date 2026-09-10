using System.Net;
using System.Text.RegularExpressions;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.OpenWeb;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CDSI.Agent.Infrastructure.OpenWeb;

public sealed class LocalOpenWebArticleContentReader : IOpenWebArticleContentReader
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".markdown", ".mdown", ".txt"
        };

    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder()
            .DisableHtml()
            .UseAdvancedExtensions()
            .Build();

    private const int MaximumInputBytes = 4 * 1024 * 1024;
    private const int MaximumOutputCharacters = 2_000_000;

    public bool Supports(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            SupportedExtensions.Contains(Path.GetExtension(path));
    }

    public async Task<OpenWebArticleContent> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Supports(path))
        {
            throw new NotSupportedException(
                "当前文章发布只支持 Markdown 和 TXT 文件。");
        }

        var source = new FileInfo(path);
        if (!source.Exists)
        {
            throw new FileNotFoundException("文章源文件不存在。", path);
        }

        var expectedSize = source.Length;
        var expectedModifiedAt = source.LastWriteTimeUtc;
        var decoded = await LocalArticleTextFileReader.ReadAsync(
            source.FullName,
            MaximumInputBytes,
            MaximumOutputCharacters,
            cancellationToken);
        if (decoded.IsTruncated)
        {
            throw new InvalidOperationException(
                "文章内容过大或编码无法完整读取，已停止发布。缩小文件或转换为 UTF-8 后重试。");
        }

        source.Refresh();
        if (!source.Exists ||
            source.Length != expectedSize ||
            source.LastWriteTimeUtc != expectedModifiedAt)
        {
            throw new IOException("文章在读取过程中发生变化，请重新发布。");
        }

        if (string.IsNullOrWhiteSpace(decoded.Text))
        {
            throw new InvalidOperationException("文章正文为空，无法发布。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var isPlainText = string.Equals(
            source.Extension,
            ".txt",
            StringComparison.OrdinalIgnoreCase);
        var markdown = decoded.Text;
        OpenWebArticleMetadata? metadata = null;
        if (!isPlainText)
        {
            var parsed = MarkdownFrontMatterParser.Parse(decoded.Text);
            markdown = parsed.Markdown;
            metadata = parsed.Metadata;
            RejectLocalImages(markdown);
        }

        var html = isPlainText
            ? RenderPlainText(decoded.Text)
            : Markdown.ToHtml(markdown, MarkdownPipeline);
        return new OpenWebArticleContent(html.Trim(), metadata);
    }

    private static void RejectLocalImages(string markdown)
    {
        var document = Markdown.Parse(markdown, MarkdownPipeline);
        var hasLocalImage = document
            .Descendants<LinkInline>()
            .Where(link => link.IsImage)
            .Select(link => link.Url)
            .Any(url =>
                string.IsNullOrWhiteSpace(url) ||
                (!url.StartsWith("//", StringComparison.Ordinal) &&
                 (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                  (uri.Scheme != Uri.UriSchemeHttp &&
                   uri.Scheme != Uri.UriSchemeHttps))));
        if (hasLocalImage)
        {
            throw new NotSupportedException(
                "文章包含本地图片。当前版本只发布文章正文，请移除本地图片后重试。");
        }
    }

    private static string RenderPlainText(string text)
    {
        var paragraphs = Regex.Split(text.Trim(), "\\n[ \\t]*\\n+")
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph))
            .Select(paragraph =>
                $"<p>{WebUtility.HtmlEncode(paragraph.Trim()).Replace("\n", "<br />\n", StringComparison.Ordinal)}</p>");
        return string.Join(Environment.NewLine, paragraphs);
    }
}
