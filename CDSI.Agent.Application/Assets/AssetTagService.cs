using System.Text;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;

namespace CDSI.Agent.Application.Assets;

public sealed class AssetTagService(IAssetTagRepository repository)
{
    public const int MaximumNameLength = 40;

    public static IReadOnlyList<string> PresetNames { get; } =
        ["素材", "文章"];

    public Task<IReadOnlyList<AssetTagSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return repository.ListAssetTagsAsync(cancellationToken);
    }

    public async Task<int> AssignAsync(
        string name,
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        var displayName = NormalizeDisplayName(name);
        var ids = NormalizeAssetIds(assetIds);
        if (ids.Length == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        return await repository.AssignAssetTagAsync(
            new AssetTag(
                Guid.NewGuid(),
                displayName,
                displayName.ToUpperInvariant(),
                now,
                now),
            ids,
            now,
            cancellationToken);
    }

    public Task<int> RemoveAsync(
        Guid tagId,
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        return repository.RemoveAssetTagAsync(
            tagId,
            NormalizeAssetIds(assetIds),
            cancellationToken);
    }

    internal static string NormalizeDisplayName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = name.Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("标签名称不能为空。", nameof(name));
        }

        if (normalized.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"标签名称不能超过 {MaximumNameLength} 个字符。",
                nameof(name));
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException("标签名称不能包含控制字符。", nameof(name));
        }

        return normalized;
    }

    private static Guid[] NormalizeAssetIds(IReadOnlyCollection<Guid> assetIds)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var ids = assetIds.Distinct().ToArray();
        if (ids.Length > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(assetIds),
                "一次最多可处理 1000 个资产的标签。");
        }

        return ids;
    }
}
