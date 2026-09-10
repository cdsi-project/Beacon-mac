namespace CDSI.Agent.Core.Assets;

public sealed record AssetListFilter
{
    public static AssetListFilter Empty { get; } = new();

    public AssetListFilter(
        AssetFileTypeFilter fileType = AssetFileTypeFilter.All,
        DateTimeOffset? createdFrom = null,
        DateTimeOffset? createdBefore = null,
        string? extension = null,
        Guid? tagId = null,
        string? filenameContains = null)
    {
        if (!Enum.IsDefined(fileType))
        {
            throw new ArgumentOutOfRangeException(nameof(fileType));
        }

        if (createdFrom is not null &&
            createdBefore is not null &&
            createdFrom >= createdBefore)
        {
            throw new ArgumentException("创建时间的结束边界必须晚于开始边界。");
        }

        FileType = fileType;
        CreatedFrom = createdFrom;
        CreatedBefore = createdBefore;
        Extension = NormalizeExtension(extension);
        TagId = tagId;
        FilenameContains = NormalizeFilenameContains(filenameContains);
    }

    public AssetFileTypeFilter FileType { get; }

    public DateTimeOffset? CreatedFrom { get; }

    public DateTimeOffset? CreatedBefore { get; }

    public string? Extension { get; }

    public Guid? TagId { get; }

    public string? FilenameContains { get; }

    public bool IsEmpty =>
        FileType == AssetFileTypeFilter.All &&
        CreatedFrom is null &&
        CreatedBefore is null &&
        Extension is null &&
        TagId is null &&
        FilenameContains is null;

    private static string? NormalizeFilenameContains(string? filenameContains)
    {
        if (string.IsNullOrWhiteSpace(filenameContains))
        {
            return null;
        }

        var normalized = filenameContains.Trim();
        if (normalized.Length > 255)
        {
            throw new ArgumentOutOfRangeException(
                nameof(filenameContains),
                "文件名搜索关键词最多允许 255 个字符。");
        }

        return normalized;
    }

    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var normalized = extension.Trim();
        if (normalized.Contains('/') || normalized.Contains('\\'))
        {
            throw new ArgumentException("扩展名不能包含路径分隔符。", nameof(extension));
        }

        if (!normalized.StartsWith('.'))
        {
            normalized = $".{normalized}";
        }

        if (normalized.Length == 1)
        {
            throw new ArgumentException("扩展名不能为空。", nameof(extension));
        }

        return normalized.ToLowerInvariant();
    }
}

public enum AssetFileTypeFilter
{
    All,
    Video,
    Audio,
    Image,
    Document,
    Other
}
