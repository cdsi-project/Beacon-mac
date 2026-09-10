using CDSI.Agent.Application.Git;
using CDSI.Agent.Application.OpenWeb;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Core.OpenWeb;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Mac.Settings;

internal enum SettingsAction
{
    BrowseWorkspace,
    ApplyWorkspace,
    AddScanRoot,
    EditScanRoot,
    RemoveScanRoot,
    AddStorageProfile,
    EditStorageProfile,
    CopyStorageEndpoint,
    CopyStorageBucket,
    DeleteStorageProfile,
    AddOpenWebSource,
    EditOpenWebSource,
    OpenOpenWebSource,
    CopyOpenWebDomain,
    DeleteOpenWebSource,
    AddGitProfile,
    EditGitProfile,
    OpenGitProvider,
    CopyGitRepositoryUrl,
    DeleteGitProfile,
    Close,
    StartInitialScan
}

internal sealed class SettingsActionRequestedEventArgs(
    SettingsAction action,
    object? context = null) : EventArgs
{
    public SettingsAction Action { get; } = action;

    public object? Context { get; } = context;
}

public sealed record SettingsWindowResult(
    bool WorkspaceChanged,
    IReadOnlyList<Guid> InitialScanRootIds,
    bool StartInitialScan);

internal sealed record SettingsOperationResult(bool Succeeded, string? Error)
{
    public static SettingsOperationResult Success { get; } = new(true, null);

    public static SettingsOperationResult Failure(string error) => new(false, error);
}

internal sealed record SettingsOperationResult<T>(
    bool Succeeded,
    T? Value,
    string? Error) where T : class
{
    public static SettingsOperationResult<T> Success(T value) => new(true, value, null);

    public static SettingsOperationResult<T> Failure(string error) =>
        new(false, null, error);
}

internal sealed record ScanRootEditorResult(
    Guid? Id,
    string Path,
    IReadOnlyList<AssetFileTypeFilter> FileTypeFilters,
    IReadOnlyList<string> ExtensionWhitelist,
    IdleScanSchedule IdleScanSchedule);

internal sealed class ScanRootSettingsRow(ScanRoot source)
{
    public ScanRoot Source { get; } = source;

    public Guid Id => Source.Id;

    public string Path => Source.Path;

    public string FileFilter => SettingsFormatting.FormatFileFilter(Source);

    public string IdleSchedule =>
        SettingsFormatting.FormatIdleScanSchedule(Source.GetIdleScanSchedule());

    public string Status => SettingsFormatting.FormatScanRootStatus(Source);

    public string LastScanned => Source.LastScannedAt?.ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm") ?? "尚未扫描";
}

internal sealed class StorageProfileSettingsRow(ConfiguredObjectStorageProfile configured)
{
    public ConfiguredObjectStorageProfile Configured { get; } = configured;

    public ObjectStorageProfile Profile => Configured.Profile;

    public Guid Id => Profile.Id;

    public string Provider => SettingsFormatting.FormatStorageProvider(Profile.Provider);

    public string DisplayName => Profile.DisplayName;

    public string Endpoint =>
        $"{(Profile.UseHttps ? "https" : "http")}://{Profile.Endpoint}";

    public string Bucket => Profile.BucketName;

    public string Region => Profile.Region ?? string.Empty;

    public string Credential => Configured.HasStoredSecret ? "已保存" : "缺失";
}

internal sealed class OpenWebSourceSettingsRow(ConfiguredOpenWebSource configured)
{
    public ConfiguredOpenWebSource Configured { get; } = configured;

    public OpenWebSource Source => Configured.Source;

    public Guid Id => Source.Id;

    public string DisplayName => Source.DisplayName;

    public string OriginDomain => Source.OriginDomain;

    public string Username => Source.WordPressUsername;

    public string Default => Source.IsDefault ? "是" : string.Empty;

    public string Credential => Configured.HasApplicationPassword ? "已保存" : "缺失";
}

internal sealed class GitProfileSettingsRow(ConfiguredGitProfile configured)
{
    public ConfiguredGitProfile Configured { get; } = configured;

    public GitProfile Profile => Configured.Profile;

    public Guid Id => Profile.Id;

    public string DisplayName => Profile.DisplayName;

    public string Provider => SettingsFormatting.FormatGitProvider(Profile.Provider);

    public string RepositoryUrl => Profile.RepositoryUrl;

    public string DefaultBranch => Profile.DefaultBranch;

    public string Authentication =>
        SettingsFormatting.FormatGitAuthentication(Profile.AuthenticationMethod);

    public string Identity => Profile.AuthenticationMethod == GitAuthenticationMethod.Password
        ? Profile.Username
        : Path.GetFileName(Profile.SshPublicKeyPath) ?? string.Empty;

    public string Default => Profile.IsDefault ? "是" : string.Empty;

    public string Credential => Profile.AuthenticationMethod == GitAuthenticationMethod.Password
        ? Configured.HasPassword ? "已保存" : "缺失"
        : "本机密钥";
}

internal static class SettingsFormatting
{
    public static string FormatFileFilter(ScanRoot root)
    {
        var filter = root.CreateFileFilter();
        var parts = new List<string>();
        if (filter.FileTypeFilters.Count == ScanFileFilter.AllFileTypes.Count)
        {
            parts.Add("全部类型");
        }
        else if (filter.FileTypeFilters.Count > 0)
        {
            parts.Add(string.Join("、", filter.FileTypeFilters.Select(FormatFileType)));
        }

        if (filter.ExtensionWhitelist.Count > 0)
        {
            var preview = string.Join(", ", filter.ExtensionWhitelist.Take(3));
            parts.Add(filter.ExtensionWhitelist.Count <= 3
                ? $"扩展名: {preview}"
                : $"扩展名: {preview} 等 {filter.ExtensionWhitelist.Count} 种");
        }

        return string.Join("；", parts);
    }

    public static string FormatIdleScanSchedule(IdleScanSchedule schedule)
    {
        if (!schedule.Enabled)
        {
            return "关闭";
        }

        var unit = schedule.Unit switch
        {
            IdleScanIntervalUnit.Minutes => "分钟",
            IdleScanIntervalUnit.Hours => "小时",
            IdleScanIntervalUnit.Days => "天",
            _ => schedule.Unit.ToString()
        };
        return $"每 {schedule.Interval} {unit}";
    }

    public static string FormatScanRootStatus(ScanRoot root)
    {
        if (!root.Enabled)
        {
            return "已停用";
        }

        return root.Status switch
        {
            ScanRootStatus.Active => "正常",
            ScanRootStatus.Unavailable => "不可用",
            ScanRootStatus.Offline => "设备离线",
            ScanRootStatus.Error => "有错误",
            _ => root.Status.ToString()
        };
    }

    public static string FormatStorageProvider(ObjectStorageProvider provider) => provider switch
    {
        ObjectStorageProvider.AliyunOss => "阿里云 OSS",
        ObjectStorageProvider.QiniuKodo => "七牛云 Kodo",
        ObjectStorageProvider.TencentCos => "腾讯云 COS",
        _ => provider.ToString()
    };

    public static string FormatGitProvider(GitHostingProvider provider) => provider switch
    {
        GitHostingProvider.GitHub => "GitHub",
        GitHostingProvider.Gitee => "Gitee（码云）",
        _ => provider.ToString()
    };

    public static string FormatGitAuthentication(GitAuthenticationMethod method) => method switch
    {
        GitAuthenticationMethod.Password => "密码",
        GitAuthenticationMethod.Ssh => "SSH",
        _ => method.ToString()
    };

    private static string FormatFileType(AssetFileTypeFilter fileType) => fileType switch
    {
        AssetFileTypeFilter.Video => "视频",
        AssetFileTypeFilter.Audio => "音频",
        AssetFileTypeFilter.Image => "图片",
        AssetFileTypeFilter.Document => "文档",
        AssetFileTypeFilter.Other => "其他",
        _ => "全部类型"
    };
}
