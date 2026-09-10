using System.Text;
using CDSI.Agent.Infrastructure.OpenWeb;

namespace CDSI.Agent.Infrastructure.Tests.OpenWeb;

public sealed class LocalOpenWebArticleContentReaderTests
{
    [Fact]
    public async Task ReadAsync_RendersMarkdownAndDisablesRawHtml()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "article.md");
        await File.WriteAllTextAsync(
            path,
            "# 标题\n\n正文包含 **重点**。\n\n<script>alert('x')</script>");
        var reader = new LocalOpenWebArticleContentReader();

        var content = await reader.ReadAsync(path);

        Assert.Contains("<h1", content.Html, StringComparison.Ordinal);
        Assert.Contains(">标题</h1>", content.Html, StringComparison.Ordinal);
        Assert.Contains("<strong>重点</strong>", content.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", content.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_ParsesAndRemovesYamlFrontMatter()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "article.md");
        await File.WriteAllTextAsync(
            path,
            """
            ---
            slug: creator-tools
            categories:
              - 创作工具
              - 教程
            tags: [CDSI, 本地优先, cdsi]
            ---
            # 正文标题

            文章正文。
            """);
        var reader = new LocalOpenWebArticleContentReader();

        var content = await reader.ReadAsync(path);

        Assert.NotNull(content.Metadata);
        Assert.Equal("creator-tools", content.Metadata.Slug);
        Assert.Equal(["创作工具", "教程"], content.Metadata.Categories);
        Assert.Equal(["CDSI", "本地优先"], content.Metadata.Tags);
        Assert.Contains("正文标题", content.Html);
        Assert.DoesNotContain("creator-tools", content.Html);
        Assert.DoesNotContain("categories", content.Html);
    }

    [Fact]
    public async Task ReadAsync_RejectsMalformedYamlFrontMatter()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "article.md");
        await File.WriteAllTextAsync(
            path,
            "---\nslug: [invalid\n---\n正文");
        var reader = new LocalOpenWebArticleContentReader();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(path));

        Assert.Contains("Front Matter", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_EncodesPlainTextAndPreservesParagraphs()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "article.txt");
        await File.WriteAllTextAsync(path, "第一行\n第二行\n\n<unsafe>");
        var reader = new LocalOpenWebArticleContentReader();

        var content = await reader.ReadAsync(path);

        Assert.Equal(
            "<p>第一行<br />\n第二行</p>" + Environment.NewLine +
            "<p>&lt;unsafe&gt;</p>",
            content.Html);
    }

    [Fact]
    public async Task ReadAsync_DecodesGb18030ForExplicitArticlePublishing()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "article.txt");
        await File.WriteAllBytesAsync(
            path,
            Encoding.GetEncoding(54936).GetBytes("中文文章正文"));
        var reader = new LocalOpenWebArticleContentReader();

        var content = await reader.ReadAsync(path);

        Assert.Contains("中文文章正文", content.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Supports_OnlyAllowsMarkdownAndPlainText()
    {
        var reader = new LocalOpenWebArticleContentReader();

        Assert.True(reader.Supports("article.md"));
        Assert.True(reader.Supports("article.txt"));
        Assert.False(reader.Supports("article.docx"));
    }

    [Fact]
    public async Task ReadAsync_RejectsLocalMarkdownImages()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "article.md");
        await File.WriteAllTextAsync(path, "![封面](images/cover.jpg)");
        var reader = new LocalOpenWebArticleContentReader();

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => reader.ReadAsync(path));

        Assert.Contains("本地图片", exception.Message, StringComparison.Ordinal);
    }
}
