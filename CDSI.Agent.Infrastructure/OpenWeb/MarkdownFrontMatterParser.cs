using CDSI.Agent.Core.OpenWeb;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CDSI.Agent.Infrastructure.OpenWeb;

internal static class MarkdownFrontMatterParser
{
    private const int MaximumFrontMatterCharacters = 64 * 1024;
    private const int MaximumSlugLength = 200;
    private const int MaximumTermCount = 50;
    private const int MaximumTermLength = 200;

    public static MarkdownFrontMatterResult Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (!markdown.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new MarkdownFrontMatterResult(markdown, null);
        }

        var yamlStart = 4;
        var lineStart = yamlStart;
        while (lineStart <= markdown.Length)
        {
            var lineEnd = markdown.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = markdown.Length;
            }

            var line = markdown.AsSpan(lineStart, lineEnd - lineStart).Trim();
            if (line.SequenceEqual("---") || line.SequenceEqual("..."))
            {
                var yamlLength = lineStart - yamlStart;
                if (yamlLength > MaximumFrontMatterCharacters)
                {
                    throw new InvalidOperationException(
                        "Markdown Front Matter 过大，最多允许 64 KB。");
                }

                var yaml = markdown.Substring(yamlStart, yamlLength);
                var bodyStart = lineEnd < markdown.Length ? lineEnd + 1 : lineEnd;
                return new MarkdownFrontMatterResult(
                    markdown[bodyStart..],
                    ParseMetadata(yaml));
            }

            if (lineEnd == markdown.Length ||
                lineEnd - yamlStart > MaximumFrontMatterCharacters)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        throw new InvalidOperationException(
            "Markdown Front Matter 缺少结束标记“---”。");
    }

    private static OpenWebArticleMetadata ParseMetadata(string yaml)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 ||
                stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                throw new InvalidOperationException(
                    "Markdown Front Matter 必须是 YAML 键值映射。");
            }

            var slug = ReadOptionalScalar(mapping, "slug");
            var categories = ReadTerms(mapping, "categories", "category");
            var tags = ReadTerms(mapping, "tags", "tag");
            return new OpenWebArticleMetadata(
                NormalizeSlug(slug),
                categories,
                tags);
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException(
                "Markdown Front Matter 的 YAML 格式无效。",
                exception);
        }
    }

    private static string? ReadOptionalScalar(
        YamlMappingNode mapping,
        string key)
    {
        var node = FindValue(mapping, key);
        if (node is null)
        {
            return null;
        }

        if (node is not YamlScalarNode scalar)
        {
            throw new InvalidOperationException(
                $"Markdown Front Matter 字段“{key}”必须是单个文本值。");
        }

        return scalar.Value;
    }

    private static IReadOnlyList<string>? ReadTerms(
        YamlMappingNode mapping,
        string pluralKey,
        string singularKey)
    {
        var pluralNode = FindValue(mapping, pluralKey);
        var singularNode = FindValue(mapping, singularKey);
        if (pluralNode is null && singularNode is null)
        {
            return null;
        }

        var terms = new List<string>();
        AddTerms(terms, pluralNode, pluralKey);
        AddTerms(terms, singularNode, singularKey);
        var normalized = terms
            .Select(NormalizeTerm)
            .Where(term => term is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > MaximumTermCount)
        {
            throw new InvalidOperationException(
                $"Markdown Front Matter 字段“{pluralKey}”最多允许 {MaximumTermCount} 项。");
        }

        return normalized;
    }

    private static void AddTerms(
        List<string> terms,
        YamlNode? node,
        string key)
    {
        switch (node)
        {
            case null:
                return;
            case YamlScalarNode scalar:
                if (!string.IsNullOrWhiteSpace(scalar.Value))
                {
                    terms.AddRange(scalar.Value.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries));
                }

                return;
            case YamlSequenceNode sequence:
                foreach (var item in sequence.Children)
                {
                    if (item is not YamlScalarNode itemScalar ||
                        itemScalar.Value is null)
                    {
                        throw new InvalidOperationException(
                            $"Markdown Front Matter 字段“{key}”只能包含文本值。");
                    }

                    terms.Add(itemScalar.Value);
                }

                return;
            default:
                throw new InvalidOperationException(
                    $"Markdown Front Matter 字段“{key}”必须是文本或文本列表。");
        }
    }

    private static YamlNode? FindValue(YamlMappingNode mapping, string key)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string? NormalizeSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var slug = value.Trim();
        if (slug.Length > MaximumSlugLength ||
            slug.Any(char.IsControl) ||
            slug.IndexOfAny(['/', '\\', '?', '#']) >= 0)
        {
            throw new InvalidOperationException(
                "Markdown Front Matter 的 slug 无效，最多允许 200 个字符且不能包含路径或查询字符。");
        }

        return slug;
    }

    private static string? NormalizeTerm(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var term = value.Trim();
        if (term.Length > MaximumTermLength || term.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"分类或标签名称最多允许 {MaximumTermLength} 个字符且不能包含控制字符。");
        }

        return term;
    }
}

internal sealed record MarkdownFrontMatterResult(
    string Markdown,
    OpenWebArticleMetadata? Metadata);
