using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CDSI.Agent.Core.Reader;

namespace CDSI.Agent.Infrastructure.Reader;

public sealed partial class SyndicationFeedParser
{
    private const int MaximumEntriesPerFetch = 1_000;
    private const long MaximumXmlCharacters = 12_000_000;

    public ReaderParsedFeed Parse(string content, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var trimmed = content.AsSpan().TrimStart();
        if (IsJson(contentType, trimmed))
        {
            return ParseJsonFeed(content);
        }

        return ParseXmlFeed(content);
    }

    private static bool IsJson(string? contentType, ReadOnlySpan<char> content)
    {
        return content.StartsWith("{".AsSpan(), StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(contentType) &&
             contentType.Contains("json", StringComparison.OrdinalIgnoreCase));
    }

    private static ReaderParsedFeed ParseXmlFeed(string content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaximumXmlCharacters
        };
        using var textReader = new StringReader(content);
        using var reader = XmlReader.Create(textReader, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("Feed XML 缺少根元素。");
        return root.Name.LocalName switch
        {
            "rss" => ParseRss(root),
            "feed" => ParseAtom(root),
            _ => throw new InvalidDataException("不支持的 XML Feed 格式。")
        };
    }

    private static ReaderParsedFeed ParseRss(XElement root)
    {
        var channel = Child(root, "channel") ??
            throw new InvalidDataException("RSS 缺少 channel 元素。");
        var entries = Children(channel, "item")
            .Take(MaximumEntriesPerFetch)
            .Select(item =>
            {
                var description = Value(item, "description");
                var content = item.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "encoded")
                    ?.Value;
                return new ReaderParsedEntry(
                    Value(item, "guid"),
                    RequiredTitle(Value(item, "title")),
                    Value(item, "link"),
                    Value(item, "creator") ?? Value(item, "author"),
                    ReaderContentText.ToPlainText(description),
                    ReaderContentText.ToPlainText(content ?? description),
                    ParseDate(Value(item, "pubDate") ?? Value(item, "date")),
                    null);
            })
            .ToArray();
        return new ReaderParsedFeed(
            RequiredTitle(Value(channel, "title")),
            Value(channel, "link"),
            ReaderContentText.ToPlainText(Value(channel, "description")),
            ReaderFeedType.Rss20,
            entries);
    }

    private static ReaderParsedFeed ParseAtom(XElement root)
    {
        var entries = Children(root, "entry")
            .Take(MaximumEntriesPerFetch)
            .Select(entry =>
            {
                var summary = Value(entry, "summary");
                var content = Value(entry, "content");
                return new ReaderParsedEntry(
                    Value(entry, "id"),
                    RequiredTitle(Value(entry, "title")),
                    AtomLink(entry),
                    Child(Child(entry, "author"), "name")?.Value,
                    ReaderContentText.ToPlainText(summary),
                    ReaderContentText.ToPlainText(content ?? summary),
                    ParseDate(Value(entry, "published")),
                    ParseDate(Value(entry, "updated")));
            })
            .ToArray();
        return new ReaderParsedFeed(
            RequiredTitle(Value(root, "title")),
            AtomLink(root),
            ReaderContentText.ToPlainText(Value(root, "subtitle")),
            ReaderFeedType.Atom,
            entries);
    }

    private static ReaderParsedFeed ParseJsonFeed(string content)
    {
        using var document = JsonDocument.Parse(
            content,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        var root = document.RootElement;
        var version = root.ValueKind == JsonValueKind.Object
            ? GetString(root, "version")
            : null;
        if (root.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(version) ||
            !version.Contains("jsonfeed.org/version", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("JSON 内容不是受支持的 JSON Feed。");
        }

        var entries = new List<ReaderParsedEntry>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray().Take(MaximumEntriesPerFetch))
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var summary = GetString(item, "summary");
                var contentText = GetString(item, "content_text");
                var contentHtml = GetString(item, "content_html");
                entries.Add(new ReaderParsedEntry(
                    GetString(item, "id"),
                    RequiredTitle(GetString(item, "title")),
                    GetString(item, "url") ?? GetString(item, "external_url"),
                    JsonAuthor(item),
                    ReaderContentText.ToPlainText(summary),
                    ReaderContentText.ToPlainText(contentText ?? contentHtml ?? summary),
                    ParseDate(GetString(item, "date_published")),
                    ParseDate(GetString(item, "date_modified"))));
            }
        }

        return new ReaderParsedFeed(
            RequiredTitle(GetString(root, "title")),
            GetString(root, "home_page_url"),
            ReaderContentText.ToPlainText(GetString(root, "description")),
            ReaderFeedType.JsonFeed,
            entries);
    }

    private static string? JsonAuthor(JsonElement item)
    {
        if (item.TryGetProperty("authors", out var authors) &&
            authors.ValueKind == JsonValueKind.Array)
        {
            return authors.EnumerateArray()
                .Where(author => author.ValueKind == JsonValueKind.Object)
                .Select(author => GetString(author, "name"))
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        }

        if (item.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
        {
            return GetString(author, "name");
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? Normalize(value.GetString())
                : null;
    }

    private static string? AtomLink(XElement element)
    {
        return Children(element, "link")
            .Select(link => new
            {
                Rel = (string?)link.Attribute("rel"),
                Href = Normalize((string?)link.Attribute("href"))
            })
            .Where(link => !string.IsNullOrWhiteSpace(link.Href))
            .OrderBy(link => string.Equals(link.Rel, "alternate", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(link => link.Href)
            .FirstOrDefault();
    }

    private static XElement? Child(XElement? element, string localName)
    {
        return element?.Elements().FirstOrDefault(child => child.Name.LocalName == localName);
    }

    private static IEnumerable<XElement> Children(XElement element, string localName)
    {
        return element.Elements().Where(child => child.Name.LocalName == localName);
    }

    private static string? Value(XElement element, string localName)
    {
        return Normalize(Child(element, localName)?.Value);
    }

    private static string RequiredTitle(string? title)
    {
        return ReaderContentText.ToPlainText(title) ?? "（无标题）";
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var parsed)
                ? parsed
                : null;
    }
}

internal static partial class ReaderContentText
{
    private static readonly Regex ScriptStyleRegex = CreateScriptStyleRegex();
    private static readonly Regex BreakRegex = CreateBreakRegex();
    private static readonly Regex TagRegex = CreateTagRegex();
    private static readonly Regex HorizontalWhitespaceRegex = CreateHorizontalWhitespaceRegex();
    private static readonly Regex ExcessLineRegex = CreateExcessLineRegex();

    public static string? ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutExecutableText = ScriptStyleRegex.Replace(value, string.Empty);
        var withBreaks = BreakRegex.Replace(withoutExecutableText, "\n");
        var withoutTags = TagRegex.Replace(withBreaks, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace("\r", string.Empty, StringComparison.Ordinal);
        var compact = HorizontalWhitespaceRegex.Replace(decoded, " ");
        compact = ExcessLineRegex.Replace(compact, "\n\n").Trim();
        return compact.Length == 0 ? null : compact;
    }

    [GeneratedRegex("(?i)</?(?:p|div|section|article|h[1-6]|li|ul|ol|blockquote|pre|br)\\b[^>]*>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CreateBreakRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CreateTagRegex();

    [GeneratedRegex("[ \\t\\f\\v]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CreateHorizontalWhitespaceRegex();

    [GeneratedRegex("\\n(?:[ ]*\\n){2,}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CreateExcessLineRegex();

    [GeneratedRegex("(?is)<(?:script|style)\\b[^>]*>.*?</(?:script|style)\\s*>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CreateScriptStyleRegex();
}
