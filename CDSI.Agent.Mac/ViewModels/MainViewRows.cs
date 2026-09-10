using CDSI.Agent.Application.Git;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Duplicates;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Reader;
using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Mac.ViewModels;

public sealed record AssetFileTypeOption(
    string DisplayName,
    AssetFileTypeFilter Value)
{
    public override string ToString() => DisplayName;
}

public sealed record AssetExtensionOption(
    string DisplayName,
    string? Value)
{
    public override string ToString() => DisplayName;
}

public sealed record AssetTagOption(
    string DisplayName,
    Guid? TagId)
{
    public override string ToString() => DisplayName;
}

public sealed record AssetRowViewModel
{
    public required AssetListItem Source { get; init; }

    public long RowNumber { get; init; }

    public string AssetIdText => DisplayFormatting.FormatAssetId(Source.AssetId);

    public string OriginalFilename => Source.OriginalFilename;

    public string ProjectNamesText => Source.ProjectNames.Count == 0
        ? "无"
        : string.Join("、", Source.ProjectNames);

    public string BackupStatusText => DisplayFormatting.FormatBackupStatus(Source);

    public string BackupTimeText => DisplayFormatting.FormatDate(Source.LatestHealthyBackupAt);

    public string TagsText => Source.Tags.Count == 0
        ? "无"
        : string.Join("、", Source.Tags);

    public string TypeText => Source.MimeType ?? "未知";

    public string SizeText => DisplayFormatting.FormatFileSize(Source.Size);

    public string Sha256 => string.IsNullOrWhiteSpace(Source.Sha256)
        ? "-"
        : Source.Sha256;

    public string ModifiedAtText => DisplayFormatting.FormatDate(Source.ModifiedAt);

    public string IndexedAtText => DisplayFormatting.FormatDate(Source.DiscoveredAt);

    public string Path => Source.Path;

    public string MediaInfoText => DisplayFormatting.FormatMetadata(Source.Metadata);

    public string LocationStatusText => DisplayFormatting.FormatAssetStatus(Source);
}

public sealed record AssetDirectoryRowViewModel
{
    public required AssetDirectorySummary Source { get; init; }

    public string Path => Source.Path;

    public string AssetCountText => Source.AssetCount.ToString("N0");

    public string AvailableAssetCountText => Source.AvailableAssetCount.ToString("N0");

    public string MissingAssetCountText => Source.MissingAssetCount.ToString("N0");

    public string AvailableSizeText => DisplayFormatting.FormatFileSize(Source.AvailableSizeBytes);

    public string LatestModifiedAtText => DisplayFormatting.FormatDate(Source.LatestModifiedAt);
}

public sealed record DuplicateAssetRowViewModel
{
    public required DuplicateAssetItem Source { get; init; }

    public int GroupNumber { get; init; }

    public required string Sha256 { get; init; }

    public string OriginalFilename => Source.OriginalFilename;

    public string SizeText { get; init; } = string.Empty;

    public string Path => Source.Path;

    public string StatusText => DisplayFormatting.FormatLocationStatus(Source.LocationStatus);

    public static IEnumerable<DuplicateAssetRowViewModel> Create(
        IReadOnlyList<ExactDuplicateGroup> groups)
    {
        return groups.SelectMany((group, index) => group.Assets.Select(asset =>
            new DuplicateAssetRowViewModel
            {
                Source = asset,
                GroupNumber = index + 1,
                Sha256 = group.Sha256.Length <= 12
                    ? group.Sha256
                    : group.Sha256[..12],
                SizeText = DisplayFormatting.FormatFileSize(group.Size)
            }));
    }
}

public sealed record ProjectRowViewModel
{
    public required AssetCollectionSummary Source { get; init; }

    public Guid Id => Source.Id;

    public string Name => Source.Name;

    public string TypeText => DisplayFormatting.FormatCollectionType(Source.Type);

    public string CloudBackupText => DisplayFormatting.FormatProjectBackupTargets(Source);

    public string CreatedAtText => DisplayFormatting.FormatDate(Source.CreatedAt);

    public string AssetCountText => Source.AssetCount.ToString("N0");

    public string SizeText => DisplayFormatting.FormatFileSize(Source.TotalSizeBytes);

    public string BackedUpCountText =>
        $"{Source.BackedUpAssetCount:N0}/{Source.AssetCount:N0}";
}

public sealed record ProjectAssetRowViewModel
{
    public required AssetCollectionMember Source { get; init; }

    public AssetListItem Asset => Source.Asset;

    public string AssetIdText => DisplayFormatting.FormatAssetId(Asset.AssetId);

    public string OriginalFilename => Asset.OriginalFilename;

    public string TypeText => Asset.MimeType ?? "未知";

    public string SizeText => DisplayFormatting.FormatFileSize(Asset.Size);

    public string AddedAtText => DisplayFormatting.FormatDate(Source.AddedAt);

    public string Path => Asset.Path;

    public string BackupStatusText => DisplayFormatting.FormatBackupStatus(Asset);
}

public sealed record CloudBackupProjectRowViewModel
{
    public required string Name { get; init; }

    public bool IsUnassigned { get; init; }

    public required IReadOnlyList<ManagedObjectStorageBackup> Backups { get; init; }

    public string ProvidersText => string.Join(
        "、",
        Backups
            .Select(backup => DisplayFormatting.FormatStorageProvider(
                backup.Profile?.Provider))
            .Distinct(StringComparer.Ordinal));

    public string AssetCountText => Backups
        .Select(backup => backup.Source.AssetId)
        .Distinct()
        .Count()
        .ToString("N0");

    public string BackupCountText => Backups.Count.ToString("N0");

    public long TotalSizeBytes => Backups.Sum(item => item.Source.Location.Size);

    public string TotalSizeText => DisplayFormatting.FormatFileSize(TotalSizeBytes);

    public DateTimeOffset LatestBackupAt => Backups.Max(
        item => item.Source.Location.CreatedAt);

    public string LatestBackupAtText => DisplayFormatting.FormatDate(LatestBackupAt);
}

public sealed record CloudBackupRowViewModel
{
    public required ManagedObjectStorageBackup Source { get; init; }

    public required string ProjectName { get; init; }

    public string LocalAssetName => Source.Source.OriginalFilename;

    public string RemoteFileName => DisplayFormatting.GetCloudFilename(
        Source.Source.Location.ObjectKey);

    public string ObjectKey => Source.Source.Location.ObjectKey;

    public string SizeText => DisplayFormatting.FormatFileSize(
        Source.Source.Location.Size);

    public string VerificationStatusText => DisplayFormatting.FormatVerificationStatus(
        Source.Source.Location.Status);

    public string BackupAtText => DisplayFormatting.FormatDate(
        Source.Source.Location.CreatedAt);
}

public sealed record GitProjectRowViewModel
{
    public required GitProjectSyncRecord Record { get; init; }

    public required string ProjectName { get; init; }

    public required AssetCollectionType ProjectType { get; init; }

    public bool LocalProjectAvailable { get; init; }

    public bool GitProfileAvailable { get; init; }

    public required string LocalStateText { get; init; }

    public string ProjectTypeText => DisplayFormatting.FormatCollectionType(ProjectType);

    public string ProfileName => Record.ProfileName;

    public string ProviderText => Record.Provider switch
    {
        GitHostingProvider.GitHub => "GitHub",
        GitHostingProvider.Gitee => "Gitee",
        _ => Record.Provider.ToString()
    };

    public string RepositoryUrl => Record.RepositoryUrl;

    public string Branch => Record.Branch;

    public string CommitIdText => Record.CommitId.Length <= 12
        ? Record.CommitId
        : Record.CommitId[..12];

    public string AssetCountText => Record.SyncedFiles.ToString("N0");

    public string SizeText => DisplayFormatting.FormatFileSize(Record.SyncedBytes);

    public string SyncedAtText => DisplayFormatting.FormatDate(Record.SyncedAt);
}

public sealed record ReaderFeedRowViewModel
{
    public required ReaderFeedSummary Source { get; init; }

    public Guid Id => Source.Feed.Id;

    public string Title => Source.Feed.Title;

    public string UnreadCountText => Source.UnreadCount.ToString("N0");
}

public sealed record ReaderNavigationRowViewModel
{
    public required string NavigationKey { get; init; }

    public required string Title { get; init; }

    public string CountText { get; init; } = string.Empty;

    public bool HasCount => CountText.Length > 0;

    public bool UnreadOnly { get; init; }

    public bool StarredOnly { get; init; }

    public ReaderFeedRowViewModel? Feed { get; init; }

    public Guid? FeedId => Feed?.Id;

    public IReadOnlyList<ReaderNavigationRowViewModel> Children { get; init; } = [];
}

public sealed record ReaderEntryRowViewModel
{
    public required ReaderEntryListItem Source { get; init; }

    public Guid Id => Source.Entry.Id;

    public string ReadStatusText => Source.Entry.IsRead ? "已读" : "未读";

    public string StarredText => Source.Entry.IsStarred ? "是" : string.Empty;

    public string Title => Source.Entry.Title;

    public string FeedTitle => Source.FeedTitle;

    public string PublishedAtText => DisplayFormatting.FormatDate(
        Source.Entry.PublishedAt ?? Source.Entry.UpdatedAt ?? Source.Entry.FetchedAt);

    public string MetadataText => string.Join(
        " · ",
        new[]
        {
            Source.FeedTitle,
            Source.Entry.Author,
            DisplayFormatting.FormatDate(Source.Entry.PublishedAt ?? Source.Entry.UpdatedAt)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string? Author => Source.Entry.Author;

    public string? Url => Source.Entry.Url;

    public string Content => Source.Entry.Content ??
        Source.Entry.Summary ??
        "该条目没有提供正文或摘要。";
}

public sealed record StatisticsViewModel(
    long AssetCount,
    long VideoAssetCount,
    long AudioAssetCount,
    long ImageAssetCount,
    long DocumentAssetCount,
    long OtherAssetCount,
    long AvailableLocalFileCount,
    long UnavailableAssetCount,
    long BackedUpAssetCount,
    long UnbackedUpAssetCount,
    string TotalSizeText,
    string BackupCoverageText,
    string VideoDurationText)
{
    public static StatisticsViewModel Empty { get; } = From(
        new AssetStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

    public static StatisticsViewModel From(AssetStatistics statistics)
    {
        return new StatisticsViewModel(
            statistics.AssetCount,
            statistics.VideoAssetCount,
            statistics.AudioAssetCount,
            statistics.ImageAssetCount,
            statistics.DocumentAssetCount,
            statistics.OtherAssetCount,
            statistics.AvailableLocalFileCount,
            statistics.UnavailableAssetCount,
            statistics.BackedUpAssetCount,
            statistics.UnbackedUpAssetCount,
            DisplayFormatting.FormatFileSize(statistics.TotalSizeBytes),
            statistics.AssetCount == 0
                ? "0.0%"
                : ((double)statistics.BackedUpAssetCount / statistics.AssetCount)
                    .ToString("P1"),
            DisplayFormatting.FormatTotalDuration(
                statistics.VideoDurationMilliseconds));
    }
}

internal static class DisplayFormatting
{
    public static string FormatAssetId(Guid assetId) => assetId.ToString("N")[^12..];

    public static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} {units[unit]}"
            : $"{value:N1} {units[unit]}";
    }

    public static string FormatDate(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";

    public static string FormatBackupStatus(AssetListItem asset)
    {
        if (!asset.HasHealthyObjectStorageBackup)
        {
            return "未备份";
        }

        var providers = asset.HealthyBackupProviders
            .Where(provider => !string.IsNullOrWhiteSpace(provider))
            .Select(FormatBackupProvider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return providers.Length == 0 ? "已备份" : string.Join("、", providers);
    }

    public static string FormatAssetStatus(AssetListItem asset)
    {
        if (asset.LocationStatus != AssetLocationStatus.Available)
        {
            return FormatLocationStatus(asset.LocationStatus);
        }

        if (asset.LocationOwnership == AssetLocationOwnership.Managed)
        {
            return "工作目录";
        }

        return asset.Status switch
        {
            AssetStatus.Indexed => "已索引",
            AssetStatus.Discovered => "已发现",
            AssetStatus.Error => "错误",
            _ => asset.Status.ToString()
        };
    }

    public static string FormatLocationStatus(AssetLocationStatus status) => status switch
    {
        AssetLocationStatus.Available => "可用",
        AssetLocationStatus.Missing => "位置缺失",
        AssetLocationStatus.Offline => "设备离线",
        AssetLocationStatus.Unverified => "位置待确认",
        _ => status.ToString()
    };

    public static string FormatMetadata(AssetMetadata? metadata)
    {
        if (metadata is null)
        {
            return "待提取";
        }

        if (metadata.Status == MetadataExtractionStatus.Unsupported)
        {
            return "无专用元数据";
        }

        if (metadata.Status == MetadataExtractionStatus.Error)
        {
            return "提取失败";
        }

        var content = metadata.Content;
        if (content is null)
        {
            return "已提取";
        }

        var parts = new List<string>();
        if (content.Width is not null && content.Height is not null)
        {
            parts.Add($"{content.Width}x{content.Height}");
        }

        if (content.DurationMilliseconds is not null)
        {
            parts.Add(FormatDuration(content.DurationMilliseconds.Value));
        }

        if (!string.IsNullOrWhiteSpace(content.VideoCodec))
        {
            parts.Add(content.VideoCodec);
        }
        else if (!string.IsNullOrWhiteSpace(content.AudioCodec))
        {
            parts.Add(content.AudioCodec);
        }

        if (parts.Count > 0)
        {
            return string.Join(" · ", parts);
        }

        return content.Kind switch
        {
            AssetMediaKind.Image => "图片",
            AssetMediaKind.Audio => "音频",
            AssetMediaKind.Video => "视频",
            _ => "已提取"
        };
    }

    public static string FormatDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";
    }

    public static string FormatTotalDuration(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "0:00";
        }

        var totalSeconds = milliseconds / 1_000;
        var totalHours = totalSeconds / 3_600;
        var minutes = totalSeconds % 3_600 / 60;
        var seconds = totalSeconds % 60;
        return totalHours > 0
            ? $"{totalHours:N0}:{minutes:00}:{seconds:00}"
            : $"{minutes}:{seconds:00}";
    }

    public static string FormatCollectionType(AssetCollectionType type) => type switch
    {
        AssetCollectionType.Video => "视频",
        AssetCollectionType.Audio => "音频",
        AssetCollectionType.Image => "图片",
        AssetCollectionType.Text => "文字",
        AssetCollectionType.Mixed => "综合",
        _ => type.ToString()
    };

    public static string FormatProjectBackupTargets(AssetCollectionSummary collection)
    {
        if (collection.BackupTargets.Count == 0)
        {
            return "未开启";
        }

        if (collection.BackupTargets.Count == 1)
        {
            var target = collection.BackupTargets[0];
            return $"{FormatStorageProvider(target.Provider)} · {target.ProfileName}";
        }

        var providers = collection.BackupTargets
            .Select(target => FormatStorageProvider(target.Provider))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return $"{collection.BackupTargets.Count:N0} 个目标 · {string.Join("、", providers)}";
    }

    public static string FormatStorageProvider(ObjectStorageProvider? provider) => provider switch
    {
        ObjectStorageProvider.AliyunOss => "阿里云 OSS",
        ObjectStorageProvider.QiniuKodo => "七牛云 Kodo",
        ObjectStorageProvider.TencentCos => "腾讯云 COS",
        null => "配置已删除",
        _ => provider.ToString()!
    };

    public static string FormatVerificationStatus(StorageVerificationStatus status) => status switch
    {
        StorageVerificationStatus.Healthy => "正常",
        StorageVerificationStatus.Missing => "云端缺失",
        StorageVerificationStatus.SizeMismatch => "大小不一致",
        StorageVerificationStatus.ChecksumMismatch => "校验不一致",
        _ => "待校验"
    };

    public static string GetCloudFilename(string objectKey)
    {
        var index = objectKey.LastIndexOf('/');
        return index < 0 ? objectKey : objectKey[(index + 1)..];
    }

    private static string FormatBackupProvider(string provider) =>
        provider.Trim().ToUpperInvariant() switch
        {
            "ALIYUNOSS" or "ALIYUN OSS" or "OSS" => "OSS",
            "QINIU" or "QINIUKODO" or "QINIU KODO" => "七牛",
            "TENCENT" or "TENCENTCOS" or "TENCENT COS" or "COS" => "COS",
            "S3" or "AMAZONS3" or "S3COMPATIBLE" => "S3",
            _ => provider.Trim()
        };
}
