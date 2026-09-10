using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using CDSI.Agent.Application.Scanning;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Core.OpenWeb;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Mac.Interaction;
using CDSI.Agent.Mac.Platform;
using CDSI.Agent.Mac.Services;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Settings;

public sealed partial class SettingsWindow : Window
{
    private SettingsViewModel? _viewModel;
    private bool _initialized;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(AppServices services) : this()
    {
        _viewModel = new SettingsViewModel(services);
        _viewModel.ActionRequested += OnActionRequested;
        DataContext = _viewModel;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    public SettingsWindow(MainViewModel viewModel)
        : this((viewModel ?? throw new ArgumentNullException(nameof(viewModel))).Services)
    {
    }

    public SettingsWindowResult CurrentResult => CreateResult(startInitialScan: false);

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized || _viewModel is null)
        {
            return;
        }

        _initialized = true;
        await _viewModel.InitializeAsync();
        if (_viewModel.ErrorMessage is not null)
        {
            await UiDialogs.ShowMessageAsync(
                this,
                "无法读取设置",
                _viewModel.ErrorMessage);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel?.IsBusy == true)
        {
            e.Cancel = true;
        }
    }

    private async void OnActionRequested(
        object? sender,
        SettingsActionRequestedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            switch (e.Action)
            {
                case SettingsAction.BrowseWorkspace:
                    await BrowseWorkspaceAsync();
                    break;
                case SettingsAction.ApplyWorkspace:
                    await ApplyWorkspaceAsync();
                    break;
                case SettingsAction.AddScanRoot:
                    await EditScanRootAsync(null);
                    break;
                case SettingsAction.EditScanRoot:
                    await EditScanRootAsync((e.Context as ScanRootSettingsRow)?.Source);
                    break;
                case SettingsAction.RemoveScanRoot:
                    await RemoveScanRootAsync(e.Context as ScanRootSettingsRow);
                    break;
                case SettingsAction.AddStorageProfile:
                    await EditStorageProfileAsync(null);
                    break;
                case SettingsAction.EditStorageProfile:
                    await EditStorageProfileAsync(
                        (e.Context as StorageProfileSettingsRow)?.Profile);
                    break;
                case SettingsAction.CopyStorageEndpoint:
                    await CopyTextAsync(
                        (e.Context as StorageProfileSettingsRow)?.Endpoint,
                        "无法复制 Endpoint");
                    break;
                case SettingsAction.CopyStorageBucket:
                    await CopyTextAsync(
                        (e.Context as StorageProfileSettingsRow)?.Bucket,
                        "无法复制 Bucket");
                    break;
                case SettingsAction.DeleteStorageProfile:
                    await DeleteStorageProfileAsync(e.Context as StorageProfileSettingsRow);
                    break;
                case SettingsAction.AddOpenWebSource:
                    await EditOpenWebSourceAsync(null);
                    break;
                case SettingsAction.EditOpenWebSource:
                    await EditOpenWebSourceAsync(
                        (e.Context as OpenWebSourceSettingsRow)?.Source);
                    break;
                case SettingsAction.OpenOpenWebSource:
                    OpenOpenWebSource(e.Context as OpenWebSourceSettingsRow);
                    break;
                case SettingsAction.CopyOpenWebDomain:
                    await CopyTextAsync(
                        (e.Context as OpenWebSourceSettingsRow)?.OriginDomain,
                        "无法复制源站域名");
                    break;
                case SettingsAction.DeleteOpenWebSource:
                    await DeleteOpenWebSourceAsync(e.Context as OpenWebSourceSettingsRow);
                    break;
                case SettingsAction.AddGitProfile:
                    await EditGitProfileAsync(null);
                    break;
                case SettingsAction.EditGitProfile:
                    await EditGitProfileAsync((e.Context as GitProfileSettingsRow)?.Profile);
                    break;
                case SettingsAction.OpenGitProvider:
                    OpenGitProvider(e.Context as GitProfileSettingsRow);
                    break;
                case SettingsAction.CopyGitRepositoryUrl:
                    await CopyTextAsync(
                        (e.Context as GitProfileSettingsRow)?.RepositoryUrl,
                        "无法复制仓库地址");
                    break;
                case SettingsAction.DeleteGitProfile:
                    await DeleteGitProfileAsync(e.Context as GitProfileSettingsRow);
                    break;
                case SettingsAction.StartInitialScan:
                    Close(CreateResult(startInitialScan: true));
                    break;
                case SettingsAction.Close:
                    Close(CreateResult(startInitialScan: false));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(e.Action));
            }
        }
        catch (Exception exception)
        {
            await UiDialogs.ShowMessageAsync(this, "设置操作失败", exception.Message);
        }
    }

    private async Task BrowseWorkspaceAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        var path = await UiDialogs.ChooseFolderAsync(
            this,
            "选择 CDSI 工作目录",
            _viewModel.WorkspacePath);
        if (path is not null)
        {
            _viewModel.WorkspacePath = path;
        }
    }

    private async Task ApplyWorkspaceAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.RequiresWorkspaceSwitchConfirmation &&
            !await UiDialogs.ConfirmAsync(
                this,
                "更改工作目录",
                "切换后不会搬移或删除旧工作目录中的文件。继续吗？",
                "继续"))
        {
            return;
        }

        var result = await _viewModel.ConfigureWorkspaceAsync();
        await ShowFailureAsync("无法设置工作目录", result);
    }

    private async Task EditScanRootAsync(ScanRoot? root)
    {
        if (_viewModel is null)
        {
            return;
        }

        ScanRootRegistrationResult? saved = null;
        await SettingsDialogs.ShowScanRootEditorAsync(
            this,
            root,
            async draft =>
            {
                var result = await _viewModel.SaveScanRootAsync(draft);
                saved = result.Value;
                return result.Error;
            });
        if (saved?.Warnings.Count > 0)
        {
            await UiDialogs.ShowMessageAsync(
                this,
                "目录重叠",
                string.Join(Environment.NewLine, saved.Warnings));
        }
    }

    private async Task RemoveScanRootAsync(ScanRootSettingsRow? row)
    {
        if (_viewModel is null || row is null ||
            !await UiDialogs.ConfirmAsync(
                this,
                "移除扫描目录",
                "移除后停止扫描此目录，已有资产和位置记录会保留。",
                "移除",
                destructive: true))
        {
            return;
        }

        var result = await _viewModel.RemoveScanRootAsync(row.Id);
        await ShowFailureAsync("无法移除扫描目录", result);
    }

    private async Task EditStorageProfileAsync(ObjectStorageProfile? profile)
    {
        if (_viewModel is null)
        {
            return;
        }

        await SettingsDialogs.ShowStorageProfileEditorAsync(
            this,
            profile,
            async request => (await _viewModel.SaveStorageProfileAsync(request)).Error);
    }

    private async Task DeleteStorageProfileAsync(StorageProfileSettingsRow? row)
    {
        if (_viewModel is null || row is null ||
            !await UiDialogs.ConfirmAsync(
                this,
                "删除备份配置",
                "将删除本机配置和 macOS 钥匙串中的凭据，不会删除 Bucket 或其中的对象。",
                "删除",
                destructive: true))
        {
            return;
        }

        var result = await _viewModel.DeleteStorageProfileAsync(row.Id);
        await ShowFailureAsync("无法删除备份配置", result);
    }

    private async Task EditOpenWebSourceAsync(OpenWebSource? source)
    {
        if (_viewModel is null)
        {
            return;
        }

        await SettingsDialogs.ShowOpenWebSourceEditorAsync(
            this,
            source,
            async request => (await _viewModel.SaveOpenWebSourceAsync(request)).Error);
    }

    private static void OpenOpenWebSource(OpenWebSourceSettingsRow? row)
    {
        if (row is not null)
        {
            MacPlatformIntegration.OpenUrl($"https://{row.OriginDomain}");
        }
    }

    private async Task DeleteOpenWebSourceAsync(OpenWebSourceSettingsRow? row)
    {
        if (_viewModel is null || row is null ||
            !await UiDialogs.ConfirmAsync(
                this,
                "删除 OpenWeb 源站",
                "将删除本机源站配置和 macOS 钥匙串中的凭据，不会删除 WordPress 中的文章。",
                "删除",
                destructive: true))
        {
            return;
        }

        var result = await _viewModel.DeleteOpenWebSourceAsync(row.Id);
        await ShowFailureAsync("无法删除 OpenWeb 源站", result);
    }

    private async Task EditGitProfileAsync(GitProfile? profile)
    {
        if (_viewModel is null)
        {
            return;
        }

        await SettingsDialogs.ShowGitProfileEditorAsync(
            this,
            profile,
            async request => (await _viewModel.SaveGitProfileAsync(request)).Error);
    }

    private static void OpenGitProvider(GitProfileSettingsRow? row)
    {
        if (row is null)
        {
            return;
        }

        MacPlatformIntegration.OpenUrl(row.Profile.Provider switch
        {
            GitHostingProvider.GitHub => "https://github.com/",
            GitHostingProvider.Gitee => "https://gitee.com/",
            _ => throw new ArgumentOutOfRangeException(nameof(row.Profile.Provider))
        });
    }

    private async Task DeleteGitProfileAsync(GitProfileSettingsRow? row)
    {
        if (_viewModel is null || row is null ||
            !await UiDialogs.ConfirmAsync(
                this,
                "删除 Git 配置",
                "将删除本机 Git 配置和 macOS 钥匙串中的凭据，不会删除远端仓库、本地文件或 SSH 密钥。",
                "删除",
                destructive: true))
        {
            return;
        }

        var result = await _viewModel.DeleteGitProfileAsync(row.Id);
        await ShowFailureAsync("无法删除 Git 配置", result);
    }

    private async Task CopyTextAsync(string? value, string errorTitle)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            await UiDialogs.ShowMessageAsync(this, errorTitle, "系统剪贴板当前不可用。");
            return;
        }

        await clipboard.SetTextAsync(value);
    }

    private async Task ShowFailureAsync(string title, SettingsOperationResult result)
    {
        if (!result.Succeeded && result.Error is not null)
        {
            await UiDialogs.ShowMessageAsync(this, title, result.Error);
        }
    }

    private SettingsWindowResult CreateResult(bool startInitialScan)
    {
        return _viewModel is null
            ? new SettingsWindowResult(false, [], false)
            : new SettingsWindowResult(
                _viewModel.WorkspaceChanged,
                _viewModel.InitialScanRootIds,
                startInitialScan);
    }

    private void OnScanRootDoubleTapped(object? sender, TappedEventArgs e)
    {
        Execute(_viewModel?.EditScanRootCommand);
    }

    private void OnStorageProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        Execute(_viewModel?.EditStorageProfileCommand);
    }

    private void OnOpenWebSourceDoubleTapped(object? sender, TappedEventArgs e)
    {
        Execute(_viewModel?.EditOpenWebSourceCommand);
    }

    private void OnGitProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        Execute(_viewModel?.EditGitProfileCommand);
    }

    private static void Execute(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }
}
