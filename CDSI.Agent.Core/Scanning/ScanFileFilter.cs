using CDSI.Agent.Core.Assets;

namespace CDSI.Agent.Core.Scanning;

public sealed class ScanFileFilter
{
    public const int MaxExtensionWhitelistCount = 256;

    public static IReadOnlyList<AssetFileTypeFilter> AllFileTypes { get; } =
        Array.AsReadOnly(
        [
            AssetFileTypeFilter.Video,
            AssetFileTypeFilter.Audio,
            AssetFileTypeFilter.Image,
            AssetFileTypeFilter.Document,
            AssetFileTypeFilter.Other
        ]);

    private readonly IReadOnlySet<string> _extensionSet;
    private readonly IReadOnlySet<AssetFileTypeFilter> _fileTypeSet;

    public ScanFileFilter(
        AssetFileTypeFilter fileTypeFilter = AssetFileTypeFilter.All,
        IEnumerable<string>? extensionWhitelist = null)
    {
        if (!Enum.IsDefined(fileTypeFilter))
        {
            throw new ArgumentOutOfRangeException(nameof(fileTypeFilter));
        }

        ExtensionWhitelist = NormalizeExtensions(extensionWhitelist);
        FileTypeFilters = ExtensionWhitelist.Count > 0
            ? Array.Empty<AssetFileTypeFilter>()
            : ExpandFileType(fileTypeFilter);
        FileTypeFilter = GetLegacyFileTypeFilter(FileTypeFilters);
        _extensionSet = new HashSet<string>(
            ExtensionWhitelist,
            StringComparer.OrdinalIgnoreCase);
        _fileTypeSet = new HashSet<AssetFileTypeFilter>(FileTypeFilters);
    }

    public ScanFileFilter(
        IEnumerable<AssetFileTypeFilter> fileTypeFilters,
        IEnumerable<string>? extensionWhitelist = null)
    {
        FileTypeFilters = NormalizeFileTypes(fileTypeFilters);
        ExtensionWhitelist = NormalizeExtensions(extensionWhitelist);
        if (FileTypeFilters.Count == 0 && ExtensionWhitelist.Count == 0)
        {
            throw new ArgumentException("请至少选择一种扫描策略。");
        }

        FileTypeFilter = GetLegacyFileTypeFilter(FileTypeFilters);
        _extensionSet = new HashSet<string>(
            ExtensionWhitelist,
            StringComparer.OrdinalIgnoreCase);
        _fileTypeSet = new HashSet<AssetFileTypeFilter>(FileTypeFilters);
    }

    public AssetFileTypeFilter FileTypeFilter { get; }

    public IReadOnlyList<AssetFileTypeFilter> FileTypeFilters { get; }

    public IReadOnlyList<string> ExtensionWhitelist { get; }

    public bool UsesExtensionWhitelist => ExtensionWhitelist.Count > 0;

    public bool Matches(string? extension, string? mimeType)
    {
        if (TryNormalizeExtension(extension, out var normalized) &&
            _extensionSet.Contains(normalized))
        {
            return true;
        }

        return _fileTypeSet.Contains(
            AssetFileTypeClassifier.Classify(extension, mimeType));
    }

    public bool HasSameConfiguration(ScanFileFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return FileTypeFilters.SequenceEqual(other.FileTypeFilters) &&
            ExtensionWhitelist.SequenceEqual(
                other.ExtensionWhitelist,
                StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<AssetFileTypeFilter> NormalizeFileTypes(
        IEnumerable<AssetFileTypeFilter> fileTypes)
    {
        ArgumentNullException.ThrowIfNull(fileTypes);
        var selected = fileTypes.ToArray();
        if (selected.Any(fileType => !Enum.IsDefined(fileType)))
        {
            throw new ArgumentOutOfRangeException(nameof(fileTypes));
        }

        if (selected.Contains(AssetFileTypeFilter.All))
        {
            return AllFileTypes;
        }

        var selectedSet = selected.ToHashSet();
        return Array.AsReadOnly(
            AllFileTypes.Where(selectedSet.Contains).ToArray());
    }

    public static IReadOnlyList<string> NormalizeExtensions(
        IEnumerable<string>? extensions)
    {
        if (extensions is null)
        {
            return Array.Empty<string>();
        }

        var normalized = extensions
            .Select(NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > MaxExtensionWhitelistCount)
        {
            throw new ArgumentException(
                $"扩展名白名单最多允许 {MaxExtensionWhitelistCount} 项。",
                nameof(extensions));
        }

        return Array.AsReadOnly(normalized);
    }

    public static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (!TryNormalizeExtension(extension, out var normalized))
        {
            throw new ArgumentException(
                "文件扩展名格式无效。示例: .mp4",
                nameof(extension));
        }

        return normalized;
    }

    private static bool TryNormalizeExtension(
        string? extension,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var candidate = extension.Trim();
        if (candidate.StartsWith("*.", StringComparison.Ordinal))
        {
            candidate = candidate[1..];
        }
        else if (!candidate.StartsWith(".", StringComparison.Ordinal))
        {
            candidate = $".{candidate}";
        }

        if (candidate.Length is < 2 or > 32 ||
            candidate.AsSpan(1).Contains('.') ||
            candidate.Any(character =>
                char.IsWhiteSpace(character) ||
                "\\/:*?\"<>|".Contains(character)))
        {
            return false;
        }

        normalized = candidate.ToLowerInvariant();
        return true;
    }

    private static IReadOnlyList<AssetFileTypeFilter> ExpandFileType(
        AssetFileTypeFilter fileTypeFilter)
    {
        return fileTypeFilter == AssetFileTypeFilter.All
            ? AllFileTypes
            : Array.AsReadOnly([fileTypeFilter]);
    }

    private static AssetFileTypeFilter GetLegacyFileTypeFilter(
        IReadOnlyList<AssetFileTypeFilter> fileTypes)
    {
        return fileTypes.Count == 1
            ? fileTypes[0]
            : AssetFileTypeFilter.All;
    }
}
