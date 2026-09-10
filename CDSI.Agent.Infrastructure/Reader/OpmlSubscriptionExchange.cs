using System.Text;
using System.Xml;
using System.Xml.Linq;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Reader;

namespace CDSI.Agent.Infrastructure.Reader;

public sealed class OpmlSubscriptionExchange : IReaderSubscriptionExchange
{
    private const long MaximumXmlCharacters = 12_000_000;

    public IReadOnlyList<ReaderSubscriptionDefinition> Parse(string opml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opml);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaximumXmlCharacters
        };
        using var textReader = new StringReader(opml);
        using var reader = XmlReader.Create(textReader, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root?.Name.LocalName != "opml")
        {
            throw new InvalidDataException("文件不是有效的 OPML。");
        }

        var body = document.Root.Elements().FirstOrDefault(element => element.Name.LocalName == "body") ??
            throw new InvalidDataException("OPML 缺少 body 元素。");
        var subscriptions = new List<ReaderSubscriptionDefinition>();
        foreach (var outline in body.Elements().Where(element => element.Name.LocalName == "outline"))
        {
            ParseOutline(outline, null, subscriptions);
        }

        return subscriptions
            .GroupBy(item => CreateImportKey(item.FeedUrl), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public string Serialize(IReadOnlyCollection<ReaderFeed> feeds)
    {
        ArgumentNullException.ThrowIfNull(feeds);
        var body = new XElement("body");
        foreach (var group in feeds
                     .Where(feed => feed.IsEnabled)
                     .OrderBy(feed => feed.FolderName)
                     .ThenBy(feed => feed.Title)
                     .GroupBy(feed => feed.FolderName?.Trim() ?? string.Empty))
        {
            var parent = body;
            if (!string.IsNullOrWhiteSpace(group.Key))
            {
                parent = new XElement("outline", new XAttribute("text", group.Key));
                body.Add(parent);
            }

            foreach (var feed in group)
            {
                var outline = new XElement(
                    "outline",
                    new XAttribute("text", feed.Title),
                    new XAttribute("title", feed.Title),
                    new XAttribute("type", "rss"),
                    new XAttribute("xmlUrl", feed.FeedUrl));
                if (!string.IsNullOrWhiteSpace(feed.SiteUrl))
                {
                    outline.Add(new XAttribute("htmlUrl", feed.SiteUrl));
                }

                parent.Add(outline);
            }
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "opml",
                new XAttribute("version", "2.0"),
                new XElement(
                    "head",
                    new XElement("title", "CDSI Beacon Reader subscriptions"),
                    new XElement("dateCreated", DateTimeOffset.UtcNow.ToString("R"))),
                body));
        var builder = new StringBuilder();
        using var writer = XmlWriter.Create(
            builder,
            new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                OmitXmlDeclaration = false
            });
        document.Save(writer);
        writer.Flush();
        return builder.ToString();
    }

    private static void ParseOutline(
        XElement outline,
        string? parentFolder,
        ICollection<ReaderSubscriptionDefinition> subscriptions)
    {
        var xmlUrl = ((string?)outline.Attribute("xmlUrl"))?.Trim();
        var title = ((string?)outline.Attribute("title") ??
                     (string?)outline.Attribute("text"))?.Trim();
        if (!string.IsNullOrWhiteSpace(xmlUrl))
        {
            subscriptions.Add(new ReaderSubscriptionDefinition(
                xmlUrl,
                title,
                ((string?)outline.Attribute("htmlUrl"))?.Trim(),
                parentFolder));
        }

        var folder = string.IsNullOrWhiteSpace(xmlUrl) && !string.IsNullOrWhiteSpace(title)
            ? string.IsNullOrWhiteSpace(parentFolder) ? title : $"{parentFolder}/{title}"
            : parentFolder;
        foreach (var child in outline.Elements().Where(element => element.Name.LocalName == "outline"))
        {
            ParseOutline(child, folder, subscriptions);
        }
    }

    private static string CreateImportKey(string feedUrl)
    {
        try
        {
            return ReaderUrl.CreateKey(feedUrl);
        }
        catch (ArgumentException)
        {
            return $"invalid:{feedUrl}";
        }
    }
}
