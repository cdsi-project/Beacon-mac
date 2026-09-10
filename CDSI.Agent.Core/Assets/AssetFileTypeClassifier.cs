namespace CDSI.Agent.Core.Assets;

public static class AssetFileTypeClassifier
{
    private static readonly IReadOnlySet<string> DocumentExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".csv", ".doc", ".docx", ".htm", ".html", ".json", ".md",
            ".odt", ".ods", ".odp", ".pdf", ".ppt", ".pptx", ".rtf",
            ".srt", ".tsv", ".txt", ".xls", ".xlsx", ".xml"
        };

    public static AssetFileTypeFilter Classify(
        string? extension,
        string? mimeType)
    {
        if (mimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return AssetFileTypeFilter.Video;
        }

        if (mimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return AssetFileTypeFilter.Audio;
        }

        if (mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return AssetFileTypeFilter.Image;
        }

        if (mimeType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
            (!string.IsNullOrWhiteSpace(extension) &&
             DocumentExtensions.Contains(NormalizeExtension(extension))))
        {
            return AssetFileTypeFilter.Document;
        }

        return AssetFileTypeFilter.Other;
    }

    public static bool Matches(
        string? extension,
        string? mimeType,
        AssetFileTypeFilter filter)
    {
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter));
        }

        return filter == AssetFileTypeFilter.All ||
            Classify(extension, mimeType) == filter;
    }

    private static string NormalizeExtension(string extension)
    {
        var normalized = extension.Trim();
        return normalized.StartsWith('.')
            ? normalized
            : $".{normalized}";
    }
}
