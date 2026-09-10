using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CDSI.Agent.Application.Assets;
using CDSI.Agent.Application.Git;
using CDSI.Agent.Application.OpenWeb;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Core.OpenWeb;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Core.Transfers;
using CDSI.Agent.Mac.Platform;
using CDSI.Agent.Mac.Services;
using CDSI.Agent.Mac.Settings;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Interaction;

/// <summary>
/// Translates UI-only commands into explicit, user-confirmed application-service calls.
/// </summary>
public sealed class InteractionCoordinator : IDisposable
{
    private static readonly FilePickerFileType OpmlFiles = new("OPML 文件")
    {
        Patterns = ["*.opml", "*.xml"]
    };

    private static readonly FilePickerFileType JsonFiles = new("JSON 文件")
    {
        Patterns = ["*.json"]
    };

    private readonly MainWindow _owner;
    private readonly MainViewModel _viewModel;
    private readonly Func<bool>? _requiresEmergencyStateRestore;
    private readonly Func<Task>? _prepareEmergencyStateRestore;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private TaskCenterWindow? _taskCenterWindow;
    private bool _isCheckingForUpdates;
    private bool _disposed;

    public InteractionCoordinator(MainWindow owner, MainViewModel viewModel)
        : this(owner, viewModel, null, null)
    {
    }

    internal InteractionCoordinator(
        MainWindow owner,
        MainViewModel viewModel,
        Func<bool>? requiresEmergencyStateRestore,
        Func<Task>? prepareEmergencyStateRestore)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _requiresEmergencyStateRestore = requiresEmergencyStateRestore;
        _prepareEmergencyStateRestore = prepareEmergencyStateRestore;
        _viewModel.UiActionRequested += OnUiActionRequested;
    }

    public event EventHandler? RestartRequested;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _viewModel.UiActionRequested -= OnUiActionRequested;
        _taskCenterWindow?.Close();
        _taskCenterWindow = null;
        _lifetimeCancellation.Dispose();
    }

    private async void OnUiActionRequested(
        object? sender,
        MainViewActionRequestedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await DispatchAsync(e);
        }
        catch (OperationCanceledException)
        {
            // User cancellation is a normal outcome for every dialog-backed action.
        }
        catch (Exception exception)
        {
            _viewModel.Services.RuntimeLog.WriteError(
                $"界面操作失败：{e.Action}",
                exception);
            await UiDialogs.ShowMessageAsync(
                _owner,
                "操作未能完成",
                exception.Message);
        }
    }

    private Task DispatchAsync(MainViewActionRequestedEventArgs request)
    {
        return request.Action switch
        {
            MainViewAction.CreateProject => CreateProjectAsync(),
            MainViewAction.OpenWorkspace => OpenPathAsync(_viewModel.WorkspacePath),
            MainViewAction.OpenDataDirectory => OpenPathAsync(
                _viewModel.Services.DataDirectory),
            MainViewAction.OpenSettings => ShowSettingsAsync(),
            MainViewAction.ShowDataProtection => ShowDataProtectionAsync(),
            MainViewAction.ShowTaskCenter => ShowTaskCenterAsync(),
            MainViewAction.ShowRuntimeLog => ShowRuntimeLogAsync(),
            MainViewAction.CheckForUpdates => CheckForUpdatesAsync(showCurrentStatus: true),
            MainViewAction.OpenDocumentation => OpenLegalFileAsync("README.md"),
            MainViewAction.ShowPrivacy => ShowPrivacyAsync(),
            MainViewAction.ShowLicense => ShowLegalFileAsync("LICENSE", "开源协议"),
            MainViewAction.ShowThirdPartyNotices => ShowLegalFileAsync(
                "THIRD-PARTY-NOTICES.md",
                "第三方许可"),
            MainViewAction.ShowAbout => ShowAboutAsync(),
            MainViewAction.OpenAssetLocation => OpenSelectedAssetLocationAsync(),
            MainViewAction.OpenAssetProject => OpenAssetProjectAsync(),
            MainViewAction.ShowAssetDetails => ShowAssetDetailsAsync(),
            MainViewAction.ManageAssetTags => ManageAssetTagsAsync(),
            MainViewAction.AddAssetsToProject => AddAssetsToProjectAsync(),
            MainViewAction.PublishAsset => PublishAssetAsync(),
            MainViewAction.CopyAssetsToWorkspace => TransferAssetsAsync(
                ManagedAssetTransferAction.Copy),
            MainViewAction.MoveAssetsToWorkspace => TransferAssetsAsync(
                ManagedAssetTransferAction.Move),
            MainViewAction.BackupAssets => BackupSelectedAssetsAsync(),
            MainViewAction.RestoreAssets => RestoreSelectedAssetsAsync(),
            MainViewAction.RemoveAssets => RemoveAssetsAsync(),
            MainViewAction.OpenAssetDirectory => OpenAssetDirectoryAsync(),
            MainViewAction.RemoveAssetDirectory => RemoveAssetDirectoryAsync(),
            MainViewAction.OpenDuplicateLocation => OpenDuplicateLocationAsync(),
            MainViewAction.ShowDuplicateDetails => ShowDuplicateDetailsAsync(),
            MainViewAction.EditProject => EditProjectAsync(),
            MainViewAction.DeleteProject => DeleteProjectsAsync(),
            MainViewAction.SyncProject => BackupSelectedProjectAsync(),
            MainViewAction.SyncProjectToGit => SyncSelectedProjectToGitAsync(),
            MainViewAction.OpenProjectAssetLocation => OpenProjectAssetLocationAsync(),
            MainViewAction.RemoveProjectAssets => RemoveProjectAssetsAsync(),
            MainViewAction.DeleteCloudBackupProject => DeleteCloudBackupProjectAsync(),
            MainViewAction.OpenCloudBackupLocation => OpenCloudBackupLocationAsync(),
            MainViewAction.RestoreCloudBackup => RestoreSelectedCloudBackupsAsync(),
            MainViewAction.DeleteCloudBackup => DeleteSelectedCloudBackupsAsync(),
            MainViewAction.SyncGitProject => SyncGitProjectAsync(),
            MainViewAction.OpenGitProject => OpenGitProjectAsync(),
            MainViewAction.OpenGitRepository => OpenGitRepositoryAsync(),
            MainViewAction.CopyGitRepositoryUrl => CopyGitRepositoryUrlAsync(),
            MainViewAction.AddReaderFeed => AddReaderFeedAsync(),
            MainViewAction.RefreshSelectedReaderFeed => RefreshReaderFeedAsync(),
            MainViewAction.OpenReaderFeedSite => OpenReaderFeedSiteAsync(),
            MainViewAction.RemoveReaderFeed => RemoveReaderFeedAsync(),
            MainViewAction.ImportReaderOpml => ImportReaderOpmlAsync(),
            MainViewAction.ExportReaderOpml => ExportReaderOpmlAsync(),
            MainViewAction.ImportReaderData => ImportReaderDataAsync(),
            MainViewAction.ExportReaderData => ExportReaderDataAsync(),
            MainViewAction.OpenReaderEntry => OpenReaderEntryAsync(),
            MainViewAction.ToggleReaderEntryRead => ToggleReaderEntryReadAsync(),
            MainViewAction.ToggleReaderEntryStar => ToggleReaderEntryStarAsync(),
            _ => Task.CompletedTask
        };
    }

    private async Task CreateProjectAsync()
    {
        await CreateProjectWithDialogAsync();
    }

    private async Task<ProjectRowViewModel?> CreateProjectWithDialogAsync()
    {
        var profiles = await _viewModel.Services.ObjectStorageProfiles.ListAsync();
        var result = await UiDialogs.ShowProjectDialogAsync(_owner, profiles);
        if (result is null)
        {
            return null;
        }

        ProjectRowViewModel? created = null;
        var completed = await RunServiceOperationAsync(
            "无法创建项目",
            "正在创建项目",
            allowCancel: false,
            async token =>
            {
                created = await _viewModel.CreateProjectAsync(
                    result.Name,
                    result.Type,
                    result.BackupProfileIds,
                    token);
            });
        return completed ? created : null;
    }

    private Task OpenPathAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            MacPlatformIntegration.OpenPath(path);
        }

        return Task.CompletedTask;
    }

    private async Task ShowSettingsAsync()
    {
        var window = new SettingsWindow(_viewModel.Services);
        var result = await window.ShowDialog<SettingsWindowResult?>(_owner) ??
            window.CurrentResult;

        if (!await RunServiceOperationAsync(
            "无法刷新设置",
            "正在应用设置",
            allowCancel: false,
            token => _viewModel.ReloadConfigurationAsync(token)))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_viewModel.WorkspacePath))
        {
            _viewModel.Services.RuntimeLog.TryUseWorkspace(
                _viewModel.WorkspacePath);
        }

        await RunServiceOperationAsync(
            "无法创建设置快照",
            "正在保护设置更改",
            allowCancel: false,
            token => _viewModel.CreateDatabaseSnapshotsAsync(
                force: false,
                token));

        if (result.StartInitialScan && result.InitialScanRootIds.Count > 0)
        {
            await _viewModel.RunIdleScanAsync(result.InitialScanRootIds);
        }
    }

    private async Task ShowDataProtectionAsync()
    {
        if (_requiresEmergencyStateRestore?.Invoke() == true &&
            _prepareEmergencyStateRestore is not null)
        {
            await _prepareEmergencyStateRestore();
            return;
        }

        if (_viewModel.IsBusy)
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "数据保护",
                "Beacon 正在执行任务，请等待任务完成后再打开数据保护。");
            return;
        }

        var restartRequested = false;
        await _viewModel.RunUiOperationAsync(
            "正在使用数据保护",
            allowCancel: false,
            async _ =>
            {
                var window = new DataProtectionWindow(_viewModel.Services);
                await window.ShowDialog(_owner);
                restartRequested = window.RestartRequested;
            });

        if (restartRequested)
        {
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private Task ShowTaskCenterAsync()
    {
        if (_taskCenterWindow is { IsVisible: true })
        {
            _taskCenterWindow.Activate();
            return Task.CompletedTask;
        }

        var window = new TaskCenterWindow(_viewModel);
        _taskCenterWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_taskCenterWindow, window))
            {
                _taskCenterWindow = null;
            }
        };
        window.Show(_owner);
        return Task.CompletedTask;
    }

    private async Task ShowRuntimeLogAsync()
    {
        var window = new RuntimeLogWindow(_viewModel.Services.RuntimeLog);
        await window.ShowDialog(_owner);
    }

    internal async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            await CheckForUpdatesAsync(showCurrentStatus: false);
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            _viewModel.Services.RuntimeLog.WriteError(
                "自动检查 Gitee 新版本失败",
                exception);
        }
    }

    private async Task CheckForUpdatesAsync(bool showCurrentStatus)
    {
        if (_isCheckingForUpdates)
        {
            if (showCurrentStatus)
            {
                await UiDialogs.ShowMessageAsync(
                    _owner,
                    "检查更新",
                    "正在检查新版本，请稍候。");
            }

            return;
        }

        _isCheckingForUpdates = true;
        ApplicationUpdateCheckResult? result = null;
        try
        {
            using var checker = new ApplicationUpdateChecker();
            if (showCurrentStatus)
            {
                if (!await RunServiceOperationAsync(
                        "无法检查更新",
                        "正在检查更新",
                        allowCancel: true,
                        async token =>
                        {
                            result = await checker.CheckAsync(
                                _viewModel.ApplicationVersion,
                                token);
                        }))
                {
                    return;
                }
            }
            else
            {
                try
                {
                    result = await checker.CheckAsync(
                        _viewModel.ApplicationVersion,
                        _lifetimeCancellation.Token);
                }
                catch (OperationCanceledException) when (_disposed)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _viewModel.Services.RuntimeLog.WriteError(
                        "自动检查 Gitee 新版本失败",
                        exception);
                    return;
                }
            }

            _viewModel.Services.RuntimeLog.WriteInformation(
                $"Gitee 版本检查完成；本地={result!.CurrentVersion}；" +
                $"远端={result.LatestVersion}；发现更新={result.IsUpdateAvailable}");
            if (_disposed || !_owner.IsVisible)
            {
                return;
            }

            if (result.IsUpdateAvailable)
            {
                if (await UiDialogs.ConfirmAsync(
                        _owner,
                        "发现新版本",
                        $"当前版本：{result.CurrentVersion}\n最新版本：{result.LatestVersion}\n\n" +
                        "Beacon 不会自动下载或执行更新。现在打开发布页？",
                        "打开发布页"))
                {
                    MacPlatformIntegration.OpenUrl(ApplicationUpdateChecker.ReleasesUrl);
                }
            }
            else if (showCurrentStatus)
            {
                await UiDialogs.ShowMessageAsync(
                    _owner,
                    "检查更新",
                    $"当前已是最新版本：{result.CurrentVersion}");
            }
        }
        finally
        {
            _isCheckingForUpdates = false;
        }
    }

    private Task OpenLegalFileAsync(string filename)
    {
        var path = FindLegalFile(filename);
        if (path is null)
        {
            throw new FileNotFoundException($"未找到 {filename}。");
        }

        MacPlatformIntegration.OpenPath(path);
        return Task.CompletedTask;
    }

    private Task ShowPrivacyAsync() => ShowTextWindowAsync(
        "数据安全与隐私",
        "CDSI Beacon 在本机发现、索引和管理资产。扫描结果、设置、RSS 数据和运行日志保存在本机；" +
        "只有在您明确执行云备份、Git 同步、OpenWeb 发布或 RSS 刷新时，才会连接相应服务。" +
        "Beacon 不会遥测上传本地路径、资产内容、凭据或客户端 ID。凭据存储在 macOS 钥匙串中。");

    private async Task ShowLegalFileAsync(string filename, string title)
    {
        var path = FindLegalFile(filename);
        var text = path is null
            ? $"未找到 {filename}。"
            : await File.ReadAllTextAsync(path);
        await ShowTextWindowAsync(title, text);
    }

    private Task ShowAboutAsync() => ShowTextWindowAsync(
        "关于 CDSI Beacon",
        $"CDSI Beacon {_viewModel.ApplicationVersion}{Environment.NewLine}{Environment.NewLine}" +
        "本地优先、隐私友好、默认非破坏性的数字资产执行端。" +
        $"{Environment.NewLine}{Environment.NewLine}客户端 ID：" +
        _viewModel.Services.ClientIdentity.Value);

    private Task OpenSelectedAssetLocationAsync()
    {
        var asset = GetSelectedAssets().FirstOrDefault();
        if (asset is not null)
        {
            MacPlatformIntegration.RevealInFinder(asset.Path);
        }

        return Task.CompletedTask;
    }

    private async Task OpenAssetProjectAsync()
    {
        var asset = GetSelectedAssets().FirstOrDefault();
        if (asset is null)
        {
            return;
        }

        var projects = _viewModel.Projects
            .Where(project => asset.ProjectNames.Contains(
                project.Name,
                StringComparer.Ordinal))
            .ToArray();
        var project = projects.Length switch
        {
            0 => null,
            1 => projects[0],
            _ => await UiDialogs.ChooseAsync(
                _owner,
                "打开所在项目",
                $"“{asset.OriginalFilename}”属于多个项目，请选择要打开的项目。",
                projects)
        };
        if (project is null)
        {
            return;
        }

        await _viewModel.SelectProjectAssetAsync(project.Id, asset.AssetId);
        _viewModel.SelectedTabIndex = 3;
    }

    private Task ShowAssetDetailsAsync()
    {
        var asset = GetSelectedAssets().FirstOrDefault();
        return asset is null
            ? Task.CompletedTask
            : ShowTextWindowAsync(
                "资产详情",
                $"文件：{asset.OriginalFilename}{Environment.NewLine}" +
                $"资产 ID：{asset.AssetId:D}{Environment.NewLine}" +
                $"类型：{asset.MimeType ?? "未知"}{Environment.NewLine}" +
                $"大小：{DisplayFormatting.FormatFileSize(asset.Size)}{Environment.NewLine}" +
                $"SHA-256：{asset.Sha256 ?? "尚未计算"}{Environment.NewLine}" +
                $"修改时间：{DisplayFormatting.FormatDate(asset.ModifiedAt)}{Environment.NewLine}" +
                $"位置状态：{DisplayFormatting.FormatLocationStatus(asset.LocationStatus)}{Environment.NewLine}" +
                $"位置：{asset.Path}");
    }

    private async Task ManageAssetTagsAsync()
    {
        var assets = GetSelectedAssets();
        if (assets.Count == 0)
        {
            return;
        }

        var known = await _viewModel.Services.AssetTags.ListAsync();
        var choices = AssetTagService.PresetNames
            .Select(name => new TagChoice(
                name,
                known.FirstOrDefault(tag => string.Equals(
                    tag.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))?.Id))
            .Concat(known
                .Where(tag => !AssetTagService.PresetNames.Contains(
                    tag.Name,
                    StringComparer.OrdinalIgnoreCase))
                .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(tag => new TagChoice(tag.Name, tag.Id)))
            .Append(TagChoice.Custom)
            .ToArray();
        var choice = await UiDialogs.ChooseAsync(
            _owner,
            "资产标签",
            $"为所选 {assets.Count:N0} 个资产添加或移除标签。",
            choices);
        if (choice is null)
        {
            return;
        }

        var name = choice.IsCustom
            ? await UiDialogs.PromptAsync(
                _owner,
                "自定义标签",
                $"输入标签名称（最多 {AssetTagService.MaximumNameLength} 个字符）")
            : choice.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var remove = choice.TagId is not null && assets.All(asset =>
            asset.Tags.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (remove && !await UiDialogs.ConfirmAsync(
                _owner,
                "移除标签",
                $"从所选 {assets.Count:N0} 个资产移除标签“{name}”？",
                "移除"))
        {
            return;
        }

        await RunServiceOperationAsync(
            remove ? "无法移除资产标签" : "无法添加资产标签",
            remove ? "正在移除资产标签" : "正在添加资产标签",
            allowCancel: false,
            async token =>
            {
                var ids = assets.Select(asset => asset.AssetId).Distinct().ToArray();
                if (remove)
                {
                    await _viewModel.Services.AssetTags.RemoveAsync(
                        choice.TagId!.Value,
                        ids,
                        token);
                }
                else
                {
                    await _viewModel.Services.AssetTags.AssignAsync(name, ids, token);
                }

                await _viewModel.RefreshAssetsAsync(token);
            });
    }

    private async Task AddAssetsToProjectAsync()
    {
        var assets = GetSelectedAssets();
        if (assets.Count == 0)
        {
            return;
        }

        var project = await ChooseOrCreateProjectAsync(
            "加入项目",
            $"选择接收 {assets.Count:N0} 个资产的项目，或新建项目。");
        if (project is null)
        {
            return;
        }

        await AddAssetsToProjectAsync(
            project,
            assets.Select(asset => asset.AssetId).Distinct().ToArray());
    }

    private async Task<bool> AddAssetsToProjectAsync(
        ProjectRowViewModel project,
        IReadOnlyCollection<Guid> assetIds)
    {
        return await RunServiceOperationAsync(
            "无法将资产加入项目",
            "正在将资产加入项目",
            allowCancel: false,
            async token =>
            {
                await _viewModel.Services.AssetCollections.AddAssetsAsync(
                    project.Id,
                    assetIds,
                    token);
                await _viewModel.RefreshProjectsAsync(token);
                await _viewModel.RefreshAssetsAsync(token);
            });
    }

    private async Task<ProjectRowViewModel?> ChooseOrCreateProjectAsync(
        string title,
        string message)
    {
        if (_viewModel.Projects.Count == 0)
        {
            return await CreateProjectWithDialogAsync();
        }

        var choices = _viewModel.Projects
            .Select(ProjectSelectionChoice.Existing)
            .Append(ProjectSelectionChoice.CreateNew)
            .ToArray();
        var choice = await UiDialogs.ChooseAsync(
            _owner,
            title,
            message,
            choices);
        return choice switch
        {
            null => null,
            { Project: { } project } => project,
            _ => await CreateProjectWithDialogAsync()
        };
    }

    private async Task PublishAssetAsync()
    {
        var assets = GetSelectedAssets();
        if (assets.Count != 1)
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "发布到 OpenWeb",
                "一次只能发布一篇文章。");
            return;
        }

        var asset = assets[0];
        if (asset.LocationStatus != AssetLocationStatus.Available ||
            !_viewModel.Services.OpenWebPublishing.Supports(asset.Path))
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "发布到 OpenWeb",
                "当前只支持发布位置可用的 Markdown 或 TXT 文章。");
            return;
        }

        var sources = (await _viewModel.Services.OpenWebSettings.ListAsync())
            .Where(source => source.HasApplicationPassword)
            .OrderByDescending(source => source.Source.IsDefault)
            .ThenBy(source => source.Source.DisplayName)
            .Select(source => new OpenWebSourceChoice(source))
            .ToArray();
        if (sources.Length == 0)
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "发布到 OpenWeb",
                "尚未配置带有效应用密码的 OpenWeb 源站，请先在设置中添加。");
            return;
        }

        var source = await UiDialogs.ChooseAsync(
            _owner,
            "选择 OpenWeb 源站",
            "文章将只发送到您明确选择的 WordPress 源站。",
            sources);
        if (source is null)
        {
            return;
        }

        var title = await UiDialogs.PromptAsync(
            _owner,
            "文章标题",
            "输入发布标题",
            Path.GetFileNameWithoutExtension(asset.OriginalFilename),
            "下一步");
        if (title is null)
        {
            return;
        }

        var status = await UiDialogs.ChooseAsync(
            _owner,
            "发布状态",
            "选择保存为草稿或直接发布。",
            PublishStatusChoice.All,
            "继续");
        if (status is null || !await UiDialogs.ConfirmAsync(
                _owner,
                "确认发布",
                $"将“{asset.OriginalFilename}”发送到“{source.Source.Source.DisplayName}”并{status.Description}。继续吗？",
                status.Status == OpenWebArticleStatus.Published ? "发布" : "保存草稿"))
        {
            return;
        }

        OpenWebArticlePublishResult? publishResult = null;
        if (!await RunServiceOperationAsync(
                "文章未能发布到 OpenWeb",
                "正在发布到 OpenWeb",
                allowCancel: true,
                async token =>
                {
                    publishResult = await _viewModel.Services.OpenWebPublishing.PublishAsync(
                        new OpenWebArticlePublishRequest(
                            asset.AssetId,
                            source.Source.Source.Id,
                            asset.Path,
                            title,
                            status.Status),
                        token);
                }))
        {
            return;
        }

        var publication = publishResult!.Publication;
        var message = publication.Status == OpenWebArticleStatus.Published
            ? $"文章已{(publishResult.WasCreated ? "创建" : "更新")}并发布。\n\n{publication.RemoteUrl}"
            : $"文章草稿已{(publishResult.WasCreated ? "创建" : "更新")}。\n\nWordPress 文章 ID：{publication.RemotePostId}";
        if (publication.Status == OpenWebArticleStatus.Published &&
            await UiDialogs.ConfirmAsync(_owner, "OpenWeb 发布完成", message, "打开文章"))
        {
            MacPlatformIntegration.OpenUrl(publication.RemoteUrl);
        }
        else if (publication.Status != OpenWebArticleStatus.Published)
        {
            await UiDialogs.ShowMessageAsync(_owner, "OpenWeb 发布完成", message);
        }
    }

    private async Task TransferAssetsAsync(ManagedAssetTransferAction action)
    {
        var assets = GetSelectedAssets();
        if (assets.Count == 0)
        {
            return;
        }

        if (assets.Any(asset => asset.LocationStatus != AssetLocationStatus.Available))
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "无法传输",
                "选择中包含不可用的本地位置，请重新选择可用文件。");
            return;
        }

        var moving = action == ManagedAssetTransferAction.Move;
        var actionText = moving ? "移动" : "复制";
        var confirmed = moving
            ? await UiDialogs.ConfirmAssetMoveAsync(_owner, assets)
            : await UiDialogs.ConfirmAsync(
                _owner,
                "复制到 CDSI 工作目录",
                $"将所选 {assets.Count:N0} 个文件复制到受管工作目录。\n\n" +
                "源文件不会被移动、改名或删除。",
                "复制");
        if (!confirmed)
        {
            return;
        }

        ManagedAssetTransferResult? result = null;
        if (!await RunServiceOperationAsync(
                $"{actionText}未能完成",
                $"正在{actionText}到 CDSI 工作目录",
                allowCancel: true,
                async token =>
                {
                    result = await _viewModel.Services.Transfers.TransferAsync(
                        assets.Select(asset => new ManagedAssetTransferRequest(
                            asset.AssetId,
                            asset.Path)).ToArray(),
                        action,
                        new Progress<ManagedAssetTransferProgress>(
                            _viewModel.UpdateTransferProgress),
                        token);
                    await RefreshAfterLocalAssetChangeAsync();
                    _viewModel.SetOperationStatus(
                        FormatTransferStatus(result, actionText));
                }))
        {
            return;
        }

        await ShowTransferResultAsync(result!, actionText);
    }

    private async Task BackupSelectedAssetsAsync()
    {
        var assets = GetSelectedAssets();
        if (assets.Count == 0)
        {
            return;
        }

        var assetIds = assets
            .Select(asset => asset.AssetId)
            .Distinct()
            .ToArray();
        var commonProjects = _viewModel.Projects
            .Where(project => assets.All(asset => asset.ProjectNames.Contains(
                project.Name,
                StringComparer.Ordinal)))
            .ToArray();
        ProjectRowViewModel? project;
        if (commonProjects.Length == 0)
        {
            project = await ChooseOrCreateProjectAsync(
                "同步到云端",
                "所选资产没有共同所属项目。请选择接收这些资产的项目，或新建项目；加入完成后将继续云端同步。");
            if (project is null || !await AddAssetsToProjectAsync(project, assetIds))
            {
                return;
            }
        }
        else
        {
            project = commonProjects.Length == 1
                ? commonProjects[0]
                : await UiDialogs.ChooseAsync(
                    _owner,
                    "选择同步项目",
                    "云端对象必须在明确的项目上下文中创建。",
                    commonProjects);
        }

        if (project is null)
        {
            return;
        }

        var plan = await _viewModel.Services.AssetCollections.PrepareSelectedSyncAsync(
            project.Id,
            assetIds);
        await BackupPlanAsync(plan.Collection.Name, plan.Assets, plan.Collection.BackupProfileIds);
    }

    private async Task BackupSelectedProjectAsync()
    {
        var project = _viewModel.SelectedProject;
        if (project is null)
        {
            return;
        }

        var plan = await _viewModel.Services.AssetCollections.PrepareSyncAsync(project.Id);
        await BackupPlanAsync(plan.Collection.Name, plan.Assets, plan.Collection.BackupProfileIds);
    }

    private async Task BackupPlanAsync(
        string projectName,
        IReadOnlyList<AssetListItem> assets,
        IReadOnlyCollection<Guid> boundProfileIds)
    {
        if (assets.Count == 0)
        {
            await UiDialogs.ShowMessageAsync(_owner, "同步到云端", "项目中没有可同步资产。");
            return;
        }

        if (assets.Any(asset => asset.LocationStatus != AssetLocationStatus.Available))
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "同步到云端",
                "项目中包含不可用的本地位置，请连接相应磁盘或重新扫描。");
            return;
        }

        var allProfiles = await _viewModel.Services.ObjectStorageProfiles.ListAsync();
        var available = allProfiles.Where(profile => profile.HasStoredSecret).ToArray();
        ConfiguredObjectStorageProfile[] selectedProfiles;
        if (boundProfileIds.Count > 0)
        {
            var requested = boundProfileIds.Distinct().ToHashSet();
            selectedProfiles = available
                .Where(profile => requested.Contains(profile.Profile.Id))
                .ToArray();
            if (selectedProfiles.Length != requested.Count)
            {
                await UiDialogs.ShowMessageAsync(
                    _owner,
                    "同步到云端",
                    "项目绑定的部分云端配置不存在或缺少有效凭据，请在设置中修复。");
                return;
            }
        }
        else
        {
            var choices = available.Select(profile => new StorageProfileChoice(profile)).ToArray();
            var selected = await UiDialogs.ChooseAsync(
                _owner,
                "选择云端备份",
                "该项目尚未绑定云端配置。本次同步使用所选配置。",
                choices);
            selectedProfiles = selected is null ? [] : [selected.Profile];
        }

        if (selectedProfiles.Length == 0)
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "同步到云端",
                "尚未配置带有效凭据的对象存储。");
            return;
        }

        if (!await UiDialogs.ConfirmAsync(
                _owner,
                "确认云端同步",
                $"将项目“{projectName}”中的 {assets.Count:N0} 个资产同步到 " +
                $"{selectedProfiles.Length:N0} 个云端配置。\n\n" +
                "Beacon 会校验同名对象，不会覆盖内容不同的远端文件。",
                "开始同步"))
        {
            return;
        }

        var results = new List<BackupTargetResult>();
        var targetErrors = new List<BackupTargetError>();
        if (!await RunServiceOperationAsync(
                "项目云端同步失败",
                $"正在同步项目：{projectName}",
                allowCancel: true,
                async token =>
                {
                    var requests = assets.Select(asset => new ObjectStorageBackupRequest(
                        asset.AssetId,
                        asset.Path,
                        ObjectDirectory: projectName)).ToArray();
                    foreach (var profile in selectedProfiles)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var progress = new Progress<ObjectStorageBackupProgress>(
                                value => _viewModel.UpdateBackupProgress(
                                    value,
                                    profile.Profile.DisplayName));
                            var result = await _viewModel.Services.ObjectStorageBackup
                                .BackupAsync(
                                    requests,
                                    profile.Profile.Id,
                                    progress,
                                    token);
                            results.Add(new BackupTargetResult(profile, result));
                            if (result.Status == UploadJobStatus.Cancelled)
                            {
                                break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            _viewModel.Services.RuntimeLog.WriteError(
                                $"云端目标 {profile.Profile.DisplayName} 同步失败",
                                exception);
                            targetErrors.Add(new BackupTargetError(
                                profile,
                                exception.Message));
                        }
                    }

                    await RefreshAfterBackupAsync();
                    _viewModel.SetOperationStatus(
                        FormatBackupStatus(
                            results,
                            targetErrors,
                            selectedProfiles.Length));
                }))
        {
            return;
        }

        await ShowBackupResultsAsync(
            results,
            targetErrors,
            selectedProfiles.Length);
    }

    private Task RestoreSelectedAssetsAsync()
    {
        var assets = GetSelectedAssets();
        var assetIds = assets
            .Select(asset => asset.AssetId)
            .Distinct()
            .ToArray();
        return assetIds.Length == 0
            ? Task.CompletedTask
            : RestoreCandidatesAsync(() =>
                _viewModel.Services.ObjectStorageRestore.ListCandidatesAsync(assetIds),
                assets.ToDictionary(
                    asset => asset.AssetId,
                    asset => asset.OriginalFilename));
    }

    private async Task RestoreCandidatesAsync(
        Func<Task<IReadOnlyList<ObjectStorageRestoreCandidate>>> loadCandidates,
        IReadOnlyDictionary<Guid, string>? expectedAssets = null)
    {
        var candidates = await loadCandidates();
        var candidateMap = candidates.ToDictionary(candidate => candidate.AssetId);
        if (expectedAssets is not null)
        {
            var safeAssetIds = candidateMap.Values
                .Where(candidate => candidate.Sources.Any(IsSafeRestoreSource))
                .Select(candidate => candidate.AssetId);
            var unavailable = FindUnavailableExpectedRestoreAssets(
                    expectedAssets,
                    safeAssetIds)
                .Take(8)
                .ToArray();
            if (unavailable.Length > 0)
            {
                await UiDialogs.ShowMessageAsync(
                    _owner,
                    "无法从云端取回",
                    "以下资产没有带有效凭据、校验正常且包含 SHA-256 的云端副本：\n\n" +
                    string.Join('\n', unavailable));
                return;
            }
        }

        var restorable = candidates
            .Select(candidate => candidate with
            {
                Sources = candidate.Sources.Where(IsSafeRestoreSource).ToArray()
            })
            .Where(candidate => candidate.Sources.Count > 0)
            .ToArray();
        if (restorable.Length != candidates.Count)
        {
            var unavailable = candidates
                .Where(candidate => !candidate.Sources.Any(IsSafeRestoreSource))
                .Select(candidate => candidate.OriginalFilename)
                .Take(8);
            await UiDialogs.ShowMessageAsync(
                _owner,
                "无法从云端取回",
                "以下资产没有带有效凭据、校验正常且包含 SHA-256 的云端副本：\n\n" +
                string.Join('\n', unavailable));
            return;
        }

        if (restorable.Length == 0)
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "从云端取回",
                "所选资产没有可用的云端备份。");
            return;
        }

        var workspace = await _viewModel.Services.Workspace.GetAsync();
        var confirmation = new RestoreConfirmationWindow(
            restorable,
            workspace?.Path);
        if (!await confirmation.ShowDialog<bool>(_owner) ||
            confirmation.Destination is null)
        {
            return;
        }

        ObjectStorageRestoreResult? result = null;
        if (!await RunServiceOperationAsync(
                "云端取回未能完成",
                "正在从云端取回资产",
                allowCancel: true,
                async token =>
                {
                    result = await _viewModel.Services.ObjectStorageRestore.RestoreAsync(
                        confirmation.SelectedRequests,
                        confirmation.Destination,
                        new Progress<ObjectStorageRestoreProgress>(
                            _viewModel.UpdateRestoreProgress),
                        token);
                    await RefreshAfterLocalAssetChangeAsync();
                    _viewModel.SetOperationStatus(FormatRestoreStatus(result));
                }))
        {
            return;
        }

        await ShowRestoreResultAsync(result!);
    }

    private async Task<ObjectStorageRestoreDestination?> ChooseRestoreDestinationAsync()
    {
        var workspace = await _viewModel.Services.Workspace.GetAsync();
        var choices = workspace is null
            ? [RestoreDestinationChoice.SelectedDirectory]
            : RestoreDestinationChoice.All;
        var choice = await UiDialogs.ChooseAsync(
            _owner,
            "选择取回位置",
            "选择受管工作目录，或指定一个本地目录。",
            choices);
        if (choice is null)
        {
            return null;
        }

        if (choice.Kind == ObjectStorageRestoreDestinationKind.ManagedWorkspace)
        {
            return new ObjectStorageRestoreDestination(choice.Kind);
        }

        var directory = await UiDialogs.ChooseFolderAsync(_owner, "选择取回目录");
        return directory is null
            ? null
            : new ObjectStorageRestoreDestination(choice.Kind, directory);
    }

    private async Task RemoveAssetsAsync()
    {
        var assets = GetSelectedAssets();
        if (assets.Count == 0 || !await UiDialogs.ConfirmAsync(
                _owner,
                "从资产列表移除",
                $"确定移除所选 {assets.Count:N0} 条记录吗？\n\n" +
                "只会从“全部资产”列表隐藏记录；本地文件、索引、项目成员关系和云端备份都不会删除。",
                "移除"))
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法移除资产记录",
            "正在移除资产记录",
            allowCancel: false,
            async token =>
            {
                await _viewModel.Services.Scan.HideAssetsFromListAsync(
                    assets.Select(asset => asset.AssetId).Distinct().ToArray(),
                    token);
                await _viewModel.RefreshAssetsAsync(token);
            });
    }

    private Task OpenAssetDirectoryAsync()
    {
        var path = _viewModel.SelectedAssetDirectory?.Path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            MacPlatformIntegration.OpenPath(path);
        }

        return Task.CompletedTask;
    }

    private async Task RemoveAssetDirectoryAsync()
    {
        var directory = _viewModel.SelectedAssetDirectory;
        if (directory is null || !await UiDialogs.ConfirmAsync(
                _owner,
                "从扫描目录中移除",
                $"从扫描目录中移除后不再扫描，也不计入资源清单。\n\n{directory.Path}\n\n" +
                "不会删除、移动或修改目录中的本地文件。",
                "移除"))
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法从扫描目录中移除",
            "正在移除扫描目录",
            allowCancel: false,
            async token =>
            {
                await _viewModel.Services.ScanRoots.ExcludeAssetDirectoryAsync(
                    directory.Path,
                    token);
                await _viewModel.RefreshAllPagesAsync(token);
            });
    }

    private Task OpenDuplicateLocationAsync()
    {
        var path = _viewModel.SelectedDuplicateAsset?.Path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            MacPlatformIntegration.RevealInFinder(path);
        }

        return Task.CompletedTask;
    }

    private Task ShowDuplicateDetailsAsync()
    {
        var duplicate = _viewModel.SelectedDuplicateAsset;
        return duplicate is null
            ? Task.CompletedTask
            : ShowTextWindowAsync(
                "重复文件详情",
                $"重复组：{duplicate.GroupNumber:N0}{Environment.NewLine}" +
                $"文件：{duplicate.OriginalFilename}{Environment.NewLine}" +
                $"SHA-256：{duplicate.Sha256}{Environment.NewLine}" +
                $"大小：{duplicate.SizeText}{Environment.NewLine}" +
                $"状态：{duplicate.StatusText}{Environment.NewLine}" +
                $"位置：{duplicate.Path}{Environment.NewLine}{Environment.NewLine}" +
                "Beacon 只报告精确重复项，不会自动删除任何文件。");
    }

    private async Task EditProjectAsync()
    {
        var project = _viewModel.SelectedProject;
        if (project is null)
        {
            return;
        }

        var result = await UiDialogs.ShowProjectDialogAsync(
            _owner,
            [],
            project.Name,
            project.Source.Type);
        if (result is null)
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法更新项目",
            "正在更新项目",
            allowCancel: false,
            async token =>
            {
                await _viewModel.Services.AssetCollections.UpdateAsync(
                    project.Id,
                    result.Name,
                    result.Type,
                    token);
                await _viewModel.RefreshProjectsAsync(token);
                await _viewModel.RefreshAssetsAsync(token);
            });
    }

    private async Task DeleteProjectsAsync()
    {
        var projects = _owner.GetSelectedProjects();
        if (projects.Count == 0)
        {
            return;
        }

        if (!await UiDialogs.ConfirmAsync(
                _owner,
                "删除项目",
                $"确定删除所选 {projects.Count:N0} 个本地项目吗？\n\n" +
                "只删除项目与成员关系；不会删除、移动或修改本地文件，也不会删除已有云端备份。",
                "删除",
                destructive: true))
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法删除项目",
            "正在删除项目",
            allowCancel: false,
            async token =>
            {
                foreach (var project in projects)
                {
                    await _viewModel.Services.AssetCollections.DeleteAsync(project.Id, token);
                }

                await _viewModel.RefreshProjectsAsync(token);
                await _viewModel.RefreshAssetsAsync(token);
                await _viewModel.RefreshGitProjectsAsync(token);
            });
    }

    private Task OpenProjectAssetLocationAsync()
    {
        var asset = _owner.GetSelectedProjectAssets().FirstOrDefault()?.Asset;
        if (asset is not null)
        {
            MacPlatformIntegration.RevealInFinder(asset.Path);
        }

        return Task.CompletedTask;
    }

    private async Task RemoveProjectAssetsAsync()
    {
        var project = _viewModel.SelectedProject;
        var assets = _owner.GetSelectedProjectAssets();
        if (project is null || assets.Count == 0 || !await UiDialogs.ConfirmAsync(
                _owner,
                "移出项目",
                $"将所选 {assets.Count:N0} 个资产移出项目“{project.Name}”？\n\n" +
                "本地文件、全部资产记录和已有云端备份都不会删除。",
                "移出"))
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法将资产移出项目",
            "正在将资产移出项目",
            allowCancel: false,
            async token =>
            {
                await _viewModel.Services.AssetCollections.RemoveAssetsAsync(
                    project.Id,
                    assets.Select(asset => asset.Asset.AssetId).Distinct().ToArray(),
                    token);
                await _viewModel.RefreshProjectsAsync(token);
                await _viewModel.RefreshAssetsAsync(token);
            });
    }

    private async Task DeleteCloudBackupProjectAsync()
    {
        var project = _viewModel.SelectedCloudBackupProject;
        if (project is null)
        {
            return;
        }

        await DeleteCloudBackupsAsync(
            project.Backups,
            $"永久删除备份项目“{project.Name}”中的 {project.Backups.Count:N0} 个云端副本？\n\n" +
            "本地项目、项目成员关系和本地文件不会删除。此操作无法撤销。",
            "删除备份项目");
    }

    private Task OpenCloudBackupLocationAsync()
    {
        var path = _viewModel.SelectedCloudBackup?.Source.Source.LocalPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            MacPlatformIntegration.RevealInFinder(path);
        }

        return Task.CompletedTask;
    }

    private Task RestoreSelectedCloudBackupsAsync()
    {
        var backups = _owner.GetSelectedCloudBackups();
        if (backups.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RestoreCandidatesAsync(() => Task.FromResult<IReadOnlyList<ObjectStorageRestoreCandidate>>(
            backups
                .GroupBy(backup => backup.Source.Source.AssetId)
                .Select(group => new ObjectStorageRestoreCandidate(
                    group.Key,
                    group.First().Source.Source.OriginalFilename,
                    group.Select(backup => new ConfiguredObjectStorageRestoreSource(
                        backup.Source.Source,
                        backup.Source.Profile!,
                        backup.Source.HasStoredSecret)).ToArray()))
                .ToArray()));
    }

    private Task DeleteSelectedCloudBackupsAsync()
    {
        var rows = _owner.GetSelectedCloudBackups();
        var backups = rows.Select(row => row.Source).ToArray();
        return backups.Length == 0
            ? Task.CompletedTask
            : DeleteCloudBackupsAsync(
                backups,
                $"永久删除所选 {backups.Length:N0} 个云端副本？\n\n" +
                "本地文件和项目关系不会删除。此操作无法撤销。",
                "删除云端备份");
    }

    private async Task DeleteCloudBackupsAsync(
        IReadOnlyCollection<ManagedObjectStorageBackup> backups,
        string prompt,
        string title)
    {
        if (backups.Count == 0 || backups.Any(backup => !backup.IsAvailable))
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                title,
                "选择中包含配置或凭据不可用的云端副本。");
            return;
        }

        if (!await UiDialogs.ConfirmAsync(
                _owner,
                title,
                prompt,
                "永久删除",
                destructive: true))
        {
            return;
        }

        var errors = new List<string>();
        await RunServiceOperationAsync(
            "无法删除云端备份",
            "正在删除云端备份",
            allowCancel: false,
            async token =>
            {
                foreach (var backup in backups)
                {
                    try
                    {
                        await _viewModel.Services.ObjectStorageManagement.DeleteAsync(
                            backup.Source.Location.Id,
                            token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        errors.Add($"{backup.Source.Location.ObjectKey}: {exception.Message}");
                    }
                }

                await _viewModel.RefreshAllPagesAsync(token);
            });
        if (errors.Count > 0)
        {
            await ShowTextWindowAsync(
                "部分云端备份未删除",
                string.Join(Environment.NewLine, errors.Take(20)));
        }
    }

    private async Task SyncSelectedProjectToGitAsync()
    {
        var project = _viewModel.SelectedProject;
        if (project is null)
        {
            return;
        }

        var profiles = (await _viewModel.Services.GitProfiles.ListAsync())
            .Where(GitProjectSyncService.IsProfileReady)
            .Select(profile => new GitProfileChoice(profile))
            .ToArray();
        if (profiles.Length == 0)
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "同步到 Git",
                "尚未配置凭据可用的 Git 仓库。");
            return;
        }

        var profile = await UiDialogs.ChooseAsync(
            _owner,
            "选择 Git 仓库",
            "选择项目要同步到的 GitHub 或 Gitee 仓库。",
            profiles);
        if (profile is not null)
        {
            await PrepareAndSyncGitAsync(project.Id, profile.Profile.Profile.Id);
        }
    }

    private Task SyncGitProjectAsync()
    {
        var gitProject = _viewModel.SelectedGitProject;
        return gitProject is null
            ? Task.CompletedTask
            : PrepareAndSyncGitAsync(
                gitProject.Record.ProjectId,
                gitProject.Record.ProfileId);
    }

    private async Task PrepareAndSyncGitAsync(Guid projectId, Guid profileId)
    {
        var preview = await _viewModel.Services.GitProjectSync.PrepareAsync(
            projectId,
            profileId);
        var largeFiles = preview.LargeFileCount == 0
            ? string.Empty
            : $"\n\n其中 {preview.LargeFileCount:N0} 个文件不小于 50 MB；远端可能拒绝，Beacon 不会自动配置 Git LFS。";
        if (!await UiDialogs.ConfirmAsync(
                _owner,
                "同步项目到 Git",
                $"项目：{preview.ProjectName}\n仓库：{preview.RepositoryUrl}\n" +
                $"分支：{preview.Branch}\n资产：{preview.AssetCount:N0} 个 · " +
                $"{DisplayFormatting.FormatFileSize(preview.TotalBytes)}{largeFiles}\n\n" +
                "Beacon 使用临时工作副本，不修改原始资产；不会覆盖仓库中未由 Beacon 管理的同名不同内容文件。",
                "开始同步"))
        {
            return;
        }

        GitProjectSyncResult? result = null;
        if (await RunServiceOperationAsync(
                "同步项目到 Git 失败",
                $"正在同步到 Git：{preview.ProjectName}",
                allowCancel: true,
                async token =>
                {
                    result = await _viewModel.Services.GitProjectSync.SyncAsync(
                        projectId,
                        profileId,
                        new Progress<GitProjectSyncProgress>(
                            _viewModel.UpdateGitSyncProgress),
                        token);
                    await TryRefreshAsync(
                        "刷新 Git 项目失败",
                        () => _viewModel.RefreshGitProjectsAsync(CancellationToken.None));
                    _viewModel.SetOperationStatus(result.CreatedCommit
                        ? $"Git 同步完成：{preview.ProjectName}"
                        : $"Git 同步完成：{preview.ProjectName}（内容未变化）");
                }))
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "Git 同步完成",
                $"分支：{result!.Branch}\n提交：{result.CommitId}" +
                (result.CreatedCommit ? string.Empty : "\n\n内容未变化，未创建新提交。"));
        }
    }

    private Task OpenGitProjectAsync()
    {
        var gitProject = _viewModel.SelectedGitProject;
        if (gitProject is null)
        {
            return Task.CompletedTask;
        }

        var project = _viewModel.Projects.FirstOrDefault(item =>
            item.Id == gitProject.Record.ProjectId);
        if (project is not null)
        {
            _viewModel.SelectedProject = project;
            _viewModel.SelectedTabIndex = 3;
        }

        return Task.CompletedTask;
    }

    private async Task OpenGitRepositoryAsync()
    {
        var repositoryUrl = _viewModel.SelectedGitProject?.RepositoryUrl;
        if (TryCreateGitRepositoryBrowserUrl(repositoryUrl, out var browserUrl))
        {
            MacPlatformIntegration.OpenUrl(browserUrl);
            return;
        }

        if (!string.IsNullOrWhiteSpace(repositoryUrl))
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                "无法打开 Git 仓库",
                "仓库地址无法转换为受支持的 GitHub 或 Gitee 网页地址。");
        }
    }

    private async Task CopyGitRepositoryUrlAsync()
    {
        var url = _viewModel.SelectedGitProject?.RepositoryUrl;
        if (!string.IsNullOrWhiteSpace(url) && _owner.Clipboard is not null)
        {
            await _owner.Clipboard.SetTextAsync(url);
        }
    }

    private async Task AddReaderFeedAsync()
    {
        var request = await UiDialogs.ShowReaderSubscriptionDialogAsync(_owner);
        if (request is null)
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法添加 RSS 订阅",
            "正在添加 RSS 订阅",
            allowCancel: true,
            async token =>
            {
                await _viewModel.SubscribeReaderAsync(request, token);
            });
    }

    private async Task RefreshReaderFeedAsync()
    {
        var feed = _viewModel.SelectedReaderFeed;
        if (feed is null)
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法刷新 RSS 订阅",
            $"正在刷新：{feed.Title}",
            allowCancel: true,
            async token =>
            {
                await _viewModel.Services.Reader.RefreshFeedAsync(feed.Id, token);
                await _viewModel.RefreshReaderAsync(token);
            });
    }

    private Task OpenReaderFeedSiteAsync()
    {
        var url = _viewModel.SelectedReaderFeed?.Source.Feed.SiteUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            MacPlatformIntegration.OpenUrl(url);
        }

        return Task.CompletedTask;
    }

    private async Task RemoveReaderFeedAsync()
    {
        var feed = _viewModel.SelectedReaderFeed;
        if (feed is null || !await UiDialogs.ConfirmAsync(
                _owner,
                "移除 RSS 订阅",
                $"移除订阅“{feed.Title}”及其本地条目？此操作不会访问或修改源站。",
                "移除",
                destructive: true))
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法移除 RSS 订阅",
            "正在移除 RSS 订阅",
            allowCancel: false,
            async token =>
            {
                await _viewModel.Services.Reader.DeleteFeedAsync(feed.Id, token);
                await _viewModel.RefreshReaderAsync(token);
            });
    }

    private async Task ImportReaderOpmlAsync()
    {
        var path = await UiDialogs.ChooseOpenFileAsync(_owner, "导入 OPML", OpmlFiles);
        if (path is null)
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法导入 OPML",
            "正在导入 OPML",
            allowCancel: true,
            async token =>
            {
                await _viewModel.Services.Reader.ImportOpmlAsync(
                    path,
                    progress: null,
                    token);
                await _viewModel.RefreshReaderAsync(token);
            });
    }

    private async Task ExportReaderOpmlAsync()
    {
        var path = await UiDialogs.ChooseSaveFileAsync(
            _owner,
            "导出 OPML",
            $"beacon-rss-subscriptions-{DateTime.Now:yyyyMMdd}.opml",
            OpmlFiles);
        if (path is null)
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法导出 OPML",
            "正在导出 OPML",
            allowCancel: false,
            token => _viewModel.Services.Reader.ExportOpmlAsync(path, token));
    }

    private async Task ImportReaderDataAsync()
    {
        var path = await UiDialogs.ChooseOpenFileAsync(
            _owner,
            "恢复 RSS 订阅数据",
            JsonFiles);
        if (path is null || !await UiDialogs.ConfirmAsync(
                _owner,
                "恢复 RSS 订阅数据",
                "恢复操作会合并订阅、条目和阅读状态；现有收藏和已读状态不会被清除。",
                "合并恢复"))
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法恢复 RSS 订阅数据",
            "正在恢复 RSS 订阅数据",
            allowCancel: true,
            async token =>
            {
                await _viewModel.Services.Reader.ImportDataAsync(path, token);
                await _viewModel.RefreshReaderAsync(token);
            });
    }

    private async Task ExportReaderDataAsync()
    {
        var path = await UiDialogs.ChooseSaveFileAsync(
            _owner,
            "备份 RSS 订阅数据",
            $"beacon-rss-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            JsonFiles);
        if (path is null)
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法备份 RSS 订阅数据",
            "正在备份 RSS 订阅数据",
            allowCancel: false,
            token => _viewModel.Services.Reader.ExportDataAsync(path, token));
    }

    private Task OpenReaderEntryAsync()
    {
        var url = _viewModel.SelectedReaderEntry?.Url;
        if (!string.IsNullOrWhiteSpace(url))
        {
            MacPlatformIntegration.OpenUrl(url);
        }

        return Task.CompletedTask;
    }

    private async Task ToggleReaderEntryReadAsync()
    {
        var entry = _viewModel.SelectedReaderEntry;
        if (entry is null)
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法更新阅读状态",
            "正在更新阅读状态",
            allowCancel: false,
            async token =>
            {
                await _viewModel.Services.Reader.SetEntryReadAsync(
                    entry.Id,
                    !entry.Source.Entry.IsRead,
                    token);
                await _viewModel.RefreshReaderAsync(token);
            });
    }

    private async Task ToggleReaderEntryStarAsync()
    {
        var entry = _viewModel.SelectedReaderEntry;
        if (entry is null)
        {
            return;
        }

        await RunServiceOperationAsync(
            "无法更新收藏状态",
            "正在更新收藏状态",
            allowCancel: false,
            async token =>
            {
                await _viewModel.Services.Reader.SetEntryStarredAsync(
                    entry.Id,
                    !entry.Source.Entry.IsStarred,
                    token);
                await _viewModel.RefreshReaderAsync(token);
            });
    }

    private IReadOnlyList<AssetListItem> GetSelectedAssets()
    {
        return _owner.GetSelectedAssets()
            .Select(row => row.Source)
            .GroupBy(asset => asset.AssetId)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task RefreshAfterLocalAssetChangeAsync()
    {
        await TryRefreshAsync(
            "刷新资产列表失败",
            () => _viewModel.RefreshAssetsAsync(CancellationToken.None));
        await TryRefreshAsync(
            "刷新资产目录失败",
            () => _viewModel.RefreshAssetDirectoriesAsync(CancellationToken.None));
        await TryRefreshAsync(
            "刷新重复项失败",
            () => _viewModel.RefreshDuplicatesAsync(CancellationToken.None));
        await TryRefreshAsync(
            "刷新项目失败",
            () => _viewModel.RefreshProjectsAsync(CancellationToken.None));
        await TryRefreshAsync(
            "刷新统计失败",
            () => _viewModel.RefreshStatisticsAsync(CancellationToken.None));
    }

    private async Task RefreshAfterBackupAsync()
    {
        await TryRefreshAsync(
            "刷新资产列表失败",
            () => _viewModel.RefreshAssetsAsync(CancellationToken.None));
        await TryRefreshAsync(
            "刷新项目失败",
            () => _viewModel.RefreshProjectsAsync(CancellationToken.None));
        await TryRefreshAsync(
            "刷新云备份失败",
            () => _viewModel.RefreshCloudBackupsAsync(CancellationToken.None));
        await TryRefreshAsync(
            "刷新统计失败",
            () => _viewModel.RefreshStatisticsAsync(CancellationToken.None));
    }

    private async Task TryRefreshAsync(string context, Func<Task> refresh)
    {
        try
        {
            await refresh();
        }
        catch (Exception exception)
        {
            _viewModel.Services.RuntimeLog.WriteError(context, exception);
        }
    }

    private Task ShowTransferResultAsync(
        ManagedAssetTransferResult result,
        string actionText)
    {
        var summary = FormatTransferStatus(result, actionText);
        return UiDialogs.ShowMessageAsync(
            _owner,
            result.Status == FileOperationStatus.Completed
                ? $"{actionText}完成"
                : result.Status == FileOperationStatus.Cancelled
                    ? $"{actionText}已取消"
                    : $"{actionText}部分完成",
            AppendItemErrors(
                summary,
                result.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.ErrorMessage))
                    .Select(item => (item.SourcePath, item.ErrorMessage!)),
                "本地操作审计"));
    }

    private static string FormatTransferStatus(
        ManagedAssetTransferResult result,
        string actionText) =>
        result.Status switch
        {
            FileOperationStatus.Completed =>
                $"{actionText}完成，共 {result.CompletedItems:N0} 个文件",
            FileOperationStatus.Cancelled =>
                $"{actionText}已取消，已完成 {result.CompletedItems:N0} 个文件",
            _ =>
                $"{actionText}完成 {result.CompletedItems:N0} 个，失败 {result.FailedItems:N0} 个"
        };

    private Task ShowBackupResultsAsync(
        IReadOnlyList<BackupTargetResult> results,
        IReadOnlyList<BackupTargetError> targetErrors,
        int totalTargets)
    {
        var summary = FormatBackupStatus(results, targetErrors, totalTargets);
        var details = results.Select(item =>
                $"{item.Profile.Profile.DisplayName}：" +
                $"{FormatUploadStatus(item.Result.Status)}，" +
                $"完成 {item.Result.CompletedItems:N0} 个，失败 {item.Result.FailedItems:N0} 个")
            .Concat(targetErrors.Select(item =>
                $"{item.Profile.Profile.DisplayName}：失败 · " +
                RuntimeLogService.RedactSensitiveText(item.ErrorMessage)));
        var message = string.Join(
            Environment.NewLine,
            new[] { summary, string.Empty }.Concat(details));
        var allCompleted = results.Count == totalTargets &&
            targetErrors.Count == 0 &&
            results.All(item => item.Result.Status == UploadJobStatus.Completed);
        return UiDialogs.ShowMessageAsync(
            _owner,
            allCompleted ? "云端同步完成" : "云端同步结果",
            message);
    }

    private static string FormatBackupStatus(
        IReadOnlyList<BackupTargetResult> results,
        IReadOnlyList<BackupTargetError> targetErrors,
        int totalTargets)
    {
        var completedTargets = results.Count(item =>
            item.Result.Status == UploadJobStatus.Completed);
        var cancelled = results.Any(item =>
            item.Result.Status == UploadJobStatus.Cancelled);
        if (cancelled)
        {
            return $"云端同步已取消，已完成 {completedTargets:N0}/{totalTargets:N0} 个目标";
        }

        if (completedTargets == totalTargets && targetErrors.Count == 0)
        {
            return $"云端同步完成，共 {totalTargets:N0} 个目标";
        }

        return
            $"云端同步完成 {completedTargets:N0}/{totalTargets:N0} 个目标，" +
            $"{totalTargets - completedTargets:N0} 个目标未完成";
    }

    private static string FormatUploadStatus(UploadJobStatus status) => status switch
    {
        UploadJobStatus.Completed => "完成",
        UploadJobStatus.Cancelled => "已取消",
        UploadJobStatus.PartiallyCompleted => "部分完成",
        UploadJobStatus.Failed => "失败",
        _ => status.ToString()
    };

    private Task ShowRestoreResultAsync(ObjectStorageRestoreResult result)
    {
        var summary = FormatRestoreStatus(result);
        return UiDialogs.ShowMessageAsync(
            _owner,
            result.Status == RestoreJobStatus.Completed
                ? "云端取回完成"
                : result.Status == RestoreJobStatus.Cancelled
                    ? "云端取回已取消"
                    : "云端取回结果",
            AppendItemErrors(
                summary,
                result.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.ErrorMessage))
                    .Select(item => (item.TargetPath, item.ErrorMessage!)),
                "本地取回审计"));
    }

    private static string FormatRestoreStatus(ObjectStorageRestoreResult result) =>
        result.Status switch
        {
            RestoreJobStatus.Completed =>
                $"云端取回完成，共 {result.CompletedItems:N0} 个资产。所有文件均通过完整性校验。",
            RestoreJobStatus.Cancelled =>
                $"云端取回已取消，已完成 {result.CompletedItems:N0} 个资产",
            _ =>
                $"云端取回完成 {result.CompletedItems:N0} 个，失败 {result.FailedItems:N0} 个"
        };

    private static string AppendItemErrors(
        string summary,
        IEnumerable<(string Path, string Error)> errors,
        string auditName)
    {
        var all = errors.ToArray();
        if (all.Length == 0)
        {
            return summary;
        }

        var lines = all.Take(8).Select(item =>
            $"{item.Path}{Environment.NewLine}" +
            RuntimeLogService.RedactSensitiveText(item.Error));
        var details = string.Join(
            Environment.NewLine + Environment.NewLine,
            lines);
        if (all.Length > 8)
        {
            details +=
                $"{Environment.NewLine}{Environment.NewLine}另有 {all.Length - 8:N0} 个错误，详情已写入{auditName}。";
        }

        return $"{summary}{Environment.NewLine}{Environment.NewLine}{details}";
    }

    private async Task<bool> RunServiceOperationAsync(
        string errorTitle,
        string status,
        bool allowCancel,
        Func<CancellationToken, Task> operation)
    {
        var completed = false;
        await _viewModel.RunUiOperationAsync(
            status,
            allowCancel,
            async cancellationToken =>
            {
                await operation(cancellationToken);
                completed = true;
            });
        if (completed && string.IsNullOrWhiteSpace(_viewModel.LastError))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_viewModel.LastError))
        {
            await UiDialogs.ShowMessageAsync(
                _owner,
                errorTitle,
                _viewModel.LastError);
        }

        return false;
    }

    private Task ShowTextWindowAsync(string title, string text) =>
        UiDialogs.ShowTextAsync(_owner, title, text);

    internal static bool TryCreateGitRepositoryBrowserUrl(
        string? repositoryUrl,
        out string browserUrl)
    {
        browserUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return false;
        }

        string host;
        string path;
        var input = repositoryUrl.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "ssh" &&
            uri.Query.Length == 0 &&
            uri.Fragment.Length == 0 &&
            uri.Host is "github.com" or "gitee.com" &&
            ((uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0) ||
             (uri.Scheme == "ssh" && uri.UserInfo == "git")))
        {
            host = uri.Host;
            path = uri.AbsolutePath.Trim('/');
        }
        else
        {
            const string githubPrefix = "git@github.com:";
            const string giteePrefix = "git@gitee.com:";
            if (input.StartsWith(githubPrefix, StringComparison.OrdinalIgnoreCase))
            {
                host = "github.com";
                path = input[githubPrefix.Length..];
            }
            else if (input.StartsWith(giteePrefix, StringComparison.OrdinalIgnoreCase))
            {
                host = "gitee.com";
                path = input[giteePrefix.Length..];
            }
            else
            {
                return false;
            }
        }

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        if (path.Length == 0 || !path.Contains('/') || path.Contains('\\') ||
            path.Contains("..", StringComparison.Ordinal) || path.Contains('?') ||
            path.Contains('#') || path.StartsWith('/') || path.EndsWith('/'))
        {
            return false;
        }

        browserUrl = $"https://{host}/{path}";
        return Uri.TryCreate(browserUrl, UriKind.Absolute, out _);
    }

    internal static IReadOnlyList<string> FindUnavailableExpectedRestoreAssets(
        IReadOnlyDictionary<Guid, string> expectedAssets,
        IEnumerable<Guid> restorableAssetIds)
    {
        ArgumentNullException.ThrowIfNull(expectedAssets);
        ArgumentNullException.ThrowIfNull(restorableAssetIds);
        var restorable = restorableAssetIds.ToHashSet();
        return expectedAssets
            .Where(item => !restorable.Contains(item.Key))
            .Select(item => item.Value)
            .ToArray();
    }

    private static bool IsSafeRestoreSource(ConfiguredObjectStorageRestoreSource source)
    {
        return source.HasStoredSecret &&
            source.Source.Location.Status == StorageVerificationStatus.Healthy &&
            !string.IsNullOrWhiteSpace(source.Source.Location.Sha256);
    }

    private static string? FindLegalFile(string filename) =>
        FindLegalFile(AppContext.BaseDirectory, filename);

    internal static string? FindLegalFile(string baseDirectory, string filename)
    {
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Legal", filename),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources", "Legal", filename)),
            Path.Combine(baseDirectory, "Resources", "Legal", filename),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "..", "win", filename))
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record TagChoice(string Name, Guid? TagId, bool IsCustom = false)
    {
        public static TagChoice Custom { get; } = new("自定义标签...", null, true);

        public override string ToString() => Name;
    }

    private sealed record StorageProfileChoice(ConfiguredObjectStorageProfile Profile)
    {
        public override string ToString() =>
            $"{Profile.Profile.DisplayName} · {DisplayFormatting.FormatStorageProvider(Profile.Profile.Provider)}";
    }

    private sealed record ProjectSelectionChoice(
        ProjectRowViewModel? Project,
        string Label)
    {
        public static ProjectSelectionChoice CreateNew { get; } = new(
            null,
            "新建项目...");

        public static ProjectSelectionChoice Existing(ProjectRowViewModel project) =>
            new(project, $"{project.Name} · {project.TypeText}");

        public override string ToString() => Label;
    }

    private sealed record OpenWebSourceChoice(ConfiguredOpenWebSource Source)
    {
        public override string ToString() =>
            $"{Source.Source.DisplayName} · {Source.Source.OriginDomain}";
    }

    private sealed record PublishStatusChoice(
        OpenWebArticleStatus Status,
        string Name,
        string Description)
    {
        public static IReadOnlyList<PublishStatusChoice> All { get; } =
        [
            new(OpenWebArticleStatus.Draft, "保存为草稿", "保存为草稿"),
            new(OpenWebArticleStatus.Published, "直接发布", "直接发布")
        ];

        public override string ToString() => Name;
    }

    private sealed record RestoreDestinationChoice(
        ObjectStorageRestoreDestinationKind Kind,
        string Name)
    {
        public static RestoreDestinationChoice SelectedDirectory { get; } = new(
            ObjectStorageRestoreDestinationKind.SelectedDirectory,
            "选择本地目录");

        public static IReadOnlyList<RestoreDestinationChoice> All { get; } =
        [
            new(ObjectStorageRestoreDestinationKind.ManagedWorkspace, "CDSI 工作目录"),
            SelectedDirectory
        ];

        public override string ToString() => Name;
    }

    private sealed record BackupTargetResult(
        ConfiguredObjectStorageProfile Profile,
        ObjectStorageBackupResult Result);

    private sealed record BackupTargetError(
        ConfiguredObjectStorageProfile Profile,
        string ErrorMessage);

    private sealed record GitProfileChoice(ConfiguredGitProfile Profile)
    {
        public override string ToString() =>
            $"{Profile.Profile.DisplayName} · {Profile.Profile.Provider} · {Profile.Profile.RepositoryUrl}";
    }
}
