using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CDSI.Agent.Application.Workspaces;
using CDSI.Agent.Infrastructure.Persistence;
using CDSI.Agent.Mac.Interaction;
using CDSI.Agent.Mac.Platform;
using CDSI.Agent.Mac.Services;
using CDSI.Agent.Mac.ViewModels;
using Avalonia.Markup.Xaml;

namespace CDSI.Agent.Mac;

public sealed class App : Avalonia.Application
{
    private AppServices? _services;
    private MainViewModel? _viewModel;
    private InteractionCoordinator? _interactionCoordinator;
    private BackgroundServiceCoordinator? _backgroundServices;
    private MainWindow? _mainWindow;
    private bool _singleInstanceListenerStarted;
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;
    private bool _skipShutdownSnapshots;
    private bool _startupNotificationShown;
    private bool _applicationInitializationFailed;

    private static readonly FilePickerFileType StateBackupFiles = new(
        "Beacon 状态备份")
    {
        Patterns = ["*.cdsibak"]
    };

    internal static MacSingleInstanceCoordinator? SingleInstance { get; set; }

    internal static ApplicationStartupState StartupState { get; set; } =
        ApplicationStartupState.Empty;

    internal static RuntimeLogService? StartupRuntimeLog { get; set; }

    internal static bool RestartForPendingStateRestore { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            _services = AppServices.CreateDefault(
                runtimeLog: StartupRuntimeLog);
            _viewModel = new MainViewModel(_services);
            _mainWindow = new MainWindow
            {
                DataContext = _viewModel
            };
            _interactionCoordinator = new InteractionCoordinator(
                _mainWindow,
                _viewModel,
                () => _applicationInitializationFailed,
                async () =>
                {
                    await TryPrepareEmergencyRestoreAsync();
                });
            _interactionCoordinator.RestartRequested += OnRestartRequested;
            _backgroundServices = new BackgroundServiceCoordinator(_viewModel);
            _mainWindow.Opened += OnMainWindowOpened;
            _mainWindow.Closing += OnMainWindowClosing;
            desktop.MainWindow = _mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (_mainWindow is null || _viewModel is null || _backgroundServices is null)
        {
            return;
        }

        if (!_singleInstanceListenerStarted && SingleInstance is not null)
        {
            SingleInstance.StartListening(ActivateMainWindow);
            _singleInstanceListenerStarted = true;
        }

        try
        {
            if (StartupState.MissingDatabases != MissingStateDatabases.None &&
                !await ResolveMissingStateDatabasesAsync(
                    StartupState.MissingDatabases))
            {
                return;
            }

            await _viewModel.InitializeAsync();
            _applicationInitializationFailed = false;
            if (_shutdownInProgress)
            {
                return;
            }

            if (_viewModel.RequiresWorkspaceSetup)
            {
                var setup = await UiDialogs.ShowWorkspaceDialogAsync(
                    _mainWindow,
                    WorkspaceApplicationService.GetSuggestedDefaultPath(),
                    firstRun: true);
                if (setup is null)
                {
                    _mainWindow.Close();
                    return;
                }

                await _viewModel.RunUiOperationAsync(
                    "正在创建 CDSI 工作目录",
                    allowCancel: false,
                    cancellationToken => _viewModel.ConfigureWorkspaceAsync(
                        setup.Path,
                        cancellationToken));
                if (_shutdownInProgress)
                {
                    return;
                }

                if (_viewModel.RequiresWorkspaceSetup)
                {
                    await UiDialogs.ShowMessageAsync(
                        _mainWindow,
                        "无法配置工作目录",
                        _viewModel.LastError ?? "工作目录配置未完成。");
                    _mainWindow.Close();
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(_viewModel.WorkspacePath))
            {
                _services?.RuntimeLog.TryUseWorkspace(_viewModel.WorkspacePath);
            }

            await ShowStartupStateRestoreNotificationAsync(
                initializationSucceeded: _services?.ReaderAvailable != false);

            if (_services?.ReaderAvailable == false &&
                await UiDialogs.ConfirmAsync(
                    _mainWindow,
                    "RSS 状态数据库不可用",
                    "资产功能可以继续使用，但本次运行已禁用 RSS。您可以选择一份 .cdsibak 状态备份进行紧急恢复；恢复会同时替换资产与 RSS 状态数据库。",
                    "选择状态备份"))
            {
                if (await TryPrepareEmergencyRestoreAsync())
                {
                    return;
                }
            }

            await _viewModel.RunUiOperationAsync(
                "正在创建启动数据库快照",
                allowCancel: false,
                cancellationToken => _viewModel.CreateDatabaseSnapshotsAsync(
                    force: false,
                    cancellationToken));

            if (!_shutdownInProgress)
            {
                _backgroundServices.Start();
                _services?.RuntimeLog.WriteInformation("CDSI Beacon 已就绪");
                if (_interactionCoordinator is not null)
                {
                    await _interactionCoordinator.CheckForUpdatesSilentlyAsync();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdownInProgress)
        {
        }
        catch (Exception exception)
        {
            _applicationInitializationFailed = true;
            _services?.RuntimeLog.WriteError("应用初始化失败", exception);
            await ShowStartupStateRestoreNotificationAsync(
                initializationSucceeded: false);
            await UiDialogs.ShowMessageAsync(
                _mainWindow,
                "无法初始化应用",
                exception.Message);
            while (!_shutdownInProgress && await UiDialogs.ConfirmAsync(
                       _mainWindow,
                       "从状态备份紧急恢复",
                       "Beacon 状态数据库初始化失败。可以选择一份已导出的 .cdsibak 状态备份执行紧急恢复；" +
                       "恢复将在重新启动时完成，当前数据库文件族会先复制到独立隔离目录。",
                       "选择状态备份"))
            {
                if (await TryPrepareEmergencyRestoreAsync())
                {
                    return;
                }
            }

            _skipShutdownSnapshots = true;
        }
    }

    private async void OnMainWindowClosing(
        object? sender,
        WindowClosingEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress || _mainWindow is null || _viewModel is null)
        {
            return;
        }

        _shutdownInProgress = true;
        _backgroundServices?.Stop();
        try
        {
            await _viewModel.PrepareForShutdownAsync(
                createSnapshots: !_skipShutdownSnapshots &&
                    !RestartForPendingStateRestore);
        }
        finally
        {
            _services?.RuntimeLog.WriteInformation("CDSI Beacon 正常退出");
            if (_interactionCoordinator is not null)
            {
                _interactionCoordinator.RestartRequested -= OnRestartRequested;
                _interactionCoordinator.Dispose();
            }
            _backgroundServices?.Dispose();
            _viewModel.Dispose();
            _shutdownCompleted = true;
            _shutdownInProgress = false;
            _mainWindow.Close();
        }
    }

    private void ActivateMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Show();
            _mainWindow.Activate();
        });
    }

    private async Task<bool> ResolveMissingStateDatabasesAsync(
        MissingStateDatabases missingDatabases)
    {
        if (_mainWindow is null || _services is null)
        {
            return false;
        }

        while (!_shutdownInProgress)
        {
            var choice = await UiDialogs.ShowMissingDatabaseRecoveryAsync(
                _mainWindow,
                CreateMissingStateDatabaseRecoveryPrompt(missingDatabases));
            if (choice == MissingDatabaseRecoveryChoice.CreateEmpty)
            {
                _services.RuntimeLog.WriteInformation(
                    $"用户确认在既有 Beacon 安装缺失 {missingDatabases} 数据库时创建空白数据库");
                return true;
            }

            if (choice == MissingDatabaseRecoveryChoice.Exit)
            {
                _skipShutdownSnapshots = true;
                _mainWindow.Close();
                return false;
            }

            if (await TryPrepareEmergencyRestoreAsync())
            {
                return false;
            }
        }

        return false;
    }

    private async Task<bool> TryPrepareEmergencyRestoreAsync()
    {
        if (_mainWindow is null || _services is null)
        {
            return false;
        }

        var path = await UiDialogs.ChooseOpenFileAsync(
            _mainWindow,
            "选择用于紧急恢复的 Beacon 状态备份",
            StateBackupFiles);
        if (path is null)
        {
            return false;
        }

        try
        {
            var backup = await _services.StateProtection.InspectAsync(path);
            if (backup.Status != LocalStateBackupStatus.Restorable)
            {
                var message = backup.Status == LocalStateBackupStatus.NewerVersion
                    ? "此备份由更高版本的 Beacon 创建。请先升级 Beacon，再执行紧急恢复。"
                    : backup.Error ?? "状态备份校验失败，文件可能已损坏或被修改。";
                await UiDialogs.ShowMessageAsync(
                    _mainWindow,
                    "无法紧急恢复",
                    message);
                return false;
            }

            if (!await UiDialogs.ConfirmAsync(
                    _mainWindow,
                    "确认紧急恢复 Beacon 状态",
                    CreateEmergencyStateRestoreConfirmation(backup),
                    "安排紧急恢复",
                    destructive: true))
            {
                return false;
            }

            var preparation = await _services.StateProtection
                .PrepareEmergencyRestoreAsync(
                    backup.Path,
                    _services.ClientIdentity.Value,
                    backup);
            _services.RuntimeLog.WriteInformation(
                $"已安排 Beacon 紧急状态恢复；RestoreId={preparation.RestoreId:D}；" +
                $"状态备份={backup.Path}；恢复前安全副本={preparation.SafetyBackupPath}");
            RequestRestartForPendingStateRestore();
            return true;
        }
        catch (Exception exception)
        {
            _services.RuntimeLog.WriteError("无法安排紧急状态恢复", exception);
            await UiDialogs.ShowMessageAsync(
                _mainWindow,
                "无法安排紧急状态恢复",
                exception.Message);
            return false;
        }
    }

    private async Task ShowStartupStateRestoreNotificationAsync(
        bool initializationSucceeded)
    {
        if (_startupNotificationShown || _mainWindow is null)
        {
            return;
        }

        _startupNotificationShown = true;
        if (StartupState.RestoreResult is { } result)
        {
            var outcome = initializationSucceeded
                ? $"Beacon 状态已恢复到 {result.BackupCreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}。"
                : "状态数据库已经替换，但 Beacon 初始化仍然失败。请查看运行日志，或选择另一份状态备份紧急恢复。";
            await UiDialogs.ShowMessageAsync(
                _mainWindow,
                initializationSucceeded ? "状态恢复完成" : "状态已替换，但初始化失败",
                $"{outcome}\n\n资产数据库和 RSS 订阅数据库已同时恢复；本地素材、云端对象、" +
                "macOS 钥匙串凭据与当前客户端 ID 未更改。\n\n" +
                $"恢复前安全副本位置：\n{result.SafetyBackupPath}");
            return;
        }

        if (string.IsNullOrWhiteSpace(StartupState.RestoreWarning))
        {
            return;
        }

        var safetyPath = string.IsNullOrWhiteSpace(
            StartupState.RestoreSafetyBackupPath)
                ? string.Empty
                : $"\n\n恢复前安全副本位置：\n{StartupState.RestoreSafetyBackupPath}";
        await UiDialogs.ShowMessageAsync(
            _mainWindow,
            initializationSucceeded ? "状态恢复未完成" : "状态恢复和初始化均未完成",
            StartupState.RestoreWarning + safetyPath);
    }

    private void OnRestartRequested(object? sender, EventArgs e) =>
        RequestRestartForPendingStateRestore();

    private void RequestRestartForPendingStateRestore()
    {
        if (RestartForPendingStateRestore)
        {
            return;
        }

        RestartForPendingStateRestore = true;
        _skipShutdownSnapshots = true;
        _services?.RuntimeLog.WriteInformation(
            "正在关闭 Beacon，以便应用待处理的状态恢复");
        _mainWindow?.Close();
    }

    internal static string CreateMissingStateDatabaseRecoveryPrompt(
        MissingStateDatabases missingDatabases)
    {
        var explanation = missingDatabases switch
        {
            MissingStateDatabases.Asset =>
                "检测到此 macOS 用户以前运行过 Beacon，但资产数据库 cdsi.db 已不存在。" +
                "直接继续会创建新的空白资产库，原有资产索引、项目、标签和发布记录不会自动恢复。",
            MissingStateDatabases.Reader =>
                "检测到此 macOS 用户以前运行过 Beacon，但 RSS 订阅数据库 reader.db 已不存在。" +
                "原有 RSS 订阅、阅读进度和收藏状态可能无法恢复。\n\n" +
                "如果这是从未使用 RSS，或从旧版本首次升级，可以创建空白 RSS 库。",
            MissingStateDatabases.Asset | MissingStateDatabases.Reader =>
                "检测到此 macOS 用户以前运行过 Beacon，但资产数据库 cdsi.db 和 RSS 订阅数据库 reader.db 均已不存在。" +
                "直接继续会创建两个空白数据库，原有资产索引、项目、标签、发布记录、RSS 订阅、阅读进度和收藏状态不会自动恢复。",
            _ => throw new ArgumentOutOfRangeException(nameof(missingDatabases))
        };

        return
            $"{explanation}\n\n" +
            "您可以从 .cdsibak 状态备份紧急恢复、明确创建缺失的空白数据库，或退出 Beacon 且不创建数据库。";
    }

    internal static string CreateEmergencyStateRestoreConfirmation(
        LocalStateBackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var createdAt = backup.CreatedAtUtc?.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm:ss") ?? "未知时间";
        return
            $"将使用 {createdAt} 创建的状态备份执行紧急恢复。\n\n" +
            "此流程不读取当前数据库。重新启动后，资产数据库、RSS 订阅数据库及其 SQLite 辅助文件会先复制到独立隔离目录，然后由所选备份同时替换。\n\n" +
            "本地素材、云端对象、macOS 钥匙串凭据、SSH 私钥和当前客户端 ID 不会被修改。\n\n" +
            "状态包中的绝对路径、RSS URL/内容、账号与连接元数据可能敏感，请作为私密数据保存。";
    }
}
