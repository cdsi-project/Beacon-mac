using System.Collections.ObjectModel;
using System.Windows.Input;
using CDSI.Agent.Application.Git;
using CDSI.Agent.Application.OpenWeb;
using CDSI.Agent.Application.Scanning;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Application.Workspaces;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Mac.Services;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Settings;

internal sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly HashSet<Guid> _initialScanRootIds = [];
    private bool _initialRootsCaptured;
    private bool _isBusy;
    private bool _workspaceChanged;
    private string? _configuredWorkspacePath;
    private string _workspacePath = string.Empty;
    private string _workspaceStatus = "尚未配置";
    private string _statusText = "正在读取设置";
    private string? _errorMessage;
    private ScanRootSettingsRow? _selectedScanRoot;
    private StorageProfileSettingsRow? _selectedStorageProfile;
    private OpenWebSourceSettingsRow? _selectedOpenWebSource;
    private GitProfileSettingsRow? _selectedGitProfile;

    public SettingsViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        BrowseWorkspaceCommand = CreateActionCommand(SettingsAction.BrowseWorkspace);
        ApplyWorkspaceCommand = CreateActionCommand(
            SettingsAction.ApplyWorkspace,
            () => !IsBusy && !string.IsNullOrWhiteSpace(WorkspacePath));

        AddScanRootCommand = CreateActionCommand(SettingsAction.AddScanRoot);
        EditScanRootCommand = CreateActionCommand(
            SettingsAction.EditScanRoot,
            () => !IsBusy && SelectedScanRoot is not null,
            () => SelectedScanRoot);
        ToggleScanRootCommand = CreateAsyncCommand(
            ToggleSelectedScanRootAsync,
            () => !IsBusy && SelectedScanRoot is not null);
        RemoveScanRootCommand = CreateActionCommand(
            SettingsAction.RemoveScanRoot,
            () => !IsBusy && SelectedScanRoot is not null,
            () => SelectedScanRoot);

        AddStorageProfileCommand = CreateActionCommand(SettingsAction.AddStorageProfile);
        EditStorageProfileCommand = CreateActionCommand(
            SettingsAction.EditStorageProfile,
            () => !IsBusy && SelectedStorageProfile is not null,
            () => SelectedStorageProfile);
        CopyStorageEndpointCommand = CreateActionCommand(
            SettingsAction.CopyStorageEndpoint,
            () => !IsBusy && SelectedStorageProfile is not null,
            () => SelectedStorageProfile);
        CopyStorageBucketCommand = CreateActionCommand(
            SettingsAction.CopyStorageBucket,
            () => !IsBusy && SelectedStorageProfile is not null,
            () => SelectedStorageProfile);
        DeleteStorageProfileCommand = CreateActionCommand(
            SettingsAction.DeleteStorageProfile,
            () => !IsBusy && SelectedStorageProfile is not null,
            () => SelectedStorageProfile);

        AddOpenWebSourceCommand = CreateActionCommand(SettingsAction.AddOpenWebSource);
        EditOpenWebSourceCommand = CreateActionCommand(
            SettingsAction.EditOpenWebSource,
            () => !IsBusy && SelectedOpenWebSource is not null,
            () => SelectedOpenWebSource);
        OpenOpenWebSourceCommand = CreateActionCommand(
            SettingsAction.OpenOpenWebSource,
            () => !IsBusy && SelectedOpenWebSource is not null,
            () => SelectedOpenWebSource);
        CopyOpenWebDomainCommand = CreateActionCommand(
            SettingsAction.CopyOpenWebDomain,
            () => !IsBusy && SelectedOpenWebSource is not null,
            () => SelectedOpenWebSource);
        SetDefaultOpenWebSourceCommand = CreateAsyncCommand(
            SetSelectedOpenWebSourceDefaultAsync,
            () => !IsBusy && SelectedOpenWebSource is { Source.IsDefault: false });
        DeleteOpenWebSourceCommand = CreateActionCommand(
            SettingsAction.DeleteOpenWebSource,
            () => !IsBusy && SelectedOpenWebSource is not null,
            () => SelectedOpenWebSource);

        AddGitProfileCommand = CreateActionCommand(SettingsAction.AddGitProfile);
        EditGitProfileCommand = CreateActionCommand(
            SettingsAction.EditGitProfile,
            () => !IsBusy && SelectedGitProfile is not null,
            () => SelectedGitProfile);
        OpenGitProviderCommand = CreateActionCommand(
            SettingsAction.OpenGitProvider,
            () => !IsBusy && SelectedGitProfile is not null,
            () => SelectedGitProfile);
        CopyGitRepositoryUrlCommand = CreateActionCommand(
            SettingsAction.CopyGitRepositoryUrl,
            () => !IsBusy && SelectedGitProfile is not null,
            () => SelectedGitProfile);
        SetDefaultGitProfileCommand = CreateAsyncCommand(
            SetSelectedGitProfileDefaultAsync,
            () => !IsBusy && SelectedGitProfile is { Profile.IsDefault: false });
        DeleteGitProfileCommand = CreateActionCommand(
            SettingsAction.DeleteGitProfile,
            () => !IsBusy && SelectedGitProfile is not null,
            () => SelectedGitProfile);

        CloseCommand = CreateActionCommand(SettingsAction.Close, () => !IsBusy);
        StartInitialScanCommand = CreateActionCommand(
            SettingsAction.StartInitialScan,
            () => !IsBusy && HasInitialScanRoots);
    }

    public event EventHandler<SettingsActionRequestedEventArgs>? ActionRequested;

    public ObservableCollection<ScanRootSettingsRow> ScanRoots { get; } = [];

    public ObservableCollection<StorageProfileSettingsRow> StorageProfiles { get; } = [];

    public ObservableCollection<OpenWebSourceSettingsRow> OpenWebSources { get; } = [];

    public ObservableCollection<GitProfileSettingsRow> GitProfiles { get; } = [];

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool WorkspaceChanged
    {
        get => _workspaceChanged;
        private set => SetProperty(ref _workspaceChanged, value);
    }

    public string WorkspacePath
    {
        get => _workspacePath;
        set
        {
            if (SetProperty(ref _workspacePath, value ?? string.Empty))
            {
                RaiseCommandStates();
            }
        }
    }

    public string WorkspaceStatus
    {
        get => _workspaceStatus;
        private set => SetProperty(ref _workspaceStatus, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public ScanRootSettingsRow? SelectedScanRoot
    {
        get => _selectedScanRoot;
        set
        {
            if (SetProperty(ref _selectedScanRoot, value))
            {
                OnPropertyChanged(nameof(ToggleScanRootText));
                RaiseCommandStates();
            }
        }
    }

    public string ToggleScanRootText => SelectedScanRoot?.Source.Enabled == true
        ? "停用"
        : "启用";

    public StorageProfileSettingsRow? SelectedStorageProfile
    {
        get => _selectedStorageProfile;
        set
        {
            if (SetProperty(ref _selectedStorageProfile, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public OpenWebSourceSettingsRow? SelectedOpenWebSource
    {
        get => _selectedOpenWebSource;
        set
        {
            if (SetProperty(ref _selectedOpenWebSource, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public GitProfileSettingsRow? SelectedGitProfile
    {
        get => _selectedGitProfile;
        set
        {
            if (SetProperty(ref _selectedGitProfile, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool HasInitialScanRoots => _initialScanRootIds.Count > 0;

    public IReadOnlyList<Guid> InitialScanRootIds => _initialScanRootIds.ToArray();

    public bool RequiresWorkspaceSwitchConfirmation
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_configuredWorkspacePath) ||
                string.IsNullOrWhiteSpace(WorkspacePath))
            {
                return false;
            }

            try
            {
                return !PathsEqual(_configuredWorkspacePath, WorkspacePath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
    }

    public ICommand BrowseWorkspaceCommand { get; }

    public ICommand ApplyWorkspaceCommand { get; }

    public ICommand AddScanRootCommand { get; }

    public ICommand EditScanRootCommand { get; }

    public ICommand ToggleScanRootCommand { get; }

    public ICommand RemoveScanRootCommand { get; }

    public ICommand AddStorageProfileCommand { get; }

    public ICommand EditStorageProfileCommand { get; }

    public ICommand CopyStorageEndpointCommand { get; }

    public ICommand CopyStorageBucketCommand { get; }

    public ICommand DeleteStorageProfileCommand { get; }

    public ICommand AddOpenWebSourceCommand { get; }

    public ICommand EditOpenWebSourceCommand { get; }

    public ICommand OpenOpenWebSourceCommand { get; }

    public ICommand CopyOpenWebDomainCommand { get; }

    public ICommand SetDefaultOpenWebSourceCommand { get; }

    public ICommand DeleteOpenWebSourceCommand { get; }

    public ICommand AddGitProfileCommand { get; }

    public ICommand EditGitProfileCommand { get; }

    public ICommand OpenGitProviderCommand { get; }

    public ICommand CopyGitRepositoryUrlCommand { get; }

    public ICommand SetDefaultGitProfileCommand { get; }

    public ICommand DeleteGitProfileCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand StartInitialScanCommand { get; }

    public async Task InitializeAsync()
    {
        await RunOperationAsync(
            async () =>
            {
                await RefreshWorkspaceCoreAsync();
                await RefreshScanRootsCoreAsync(captureInitialRoots: true);
                await RefreshStorageProfilesCoreAsync();
                await RefreshOpenWebSourcesCoreAsync();
                await RefreshGitProfilesCoreAsync();
            },
            "设置已加载");
    }

    public Task<SettingsOperationResult> ConfigureWorkspaceAsync()
    {
        return RunOperationAsync(
            async () =>
            {
                var path = WorkspacePath.Trim();
                var changed = _configuredWorkspacePath is null ||
                    !PathsEqual(_configuredWorkspacePath, path);
                var result = await _services.Workspace.ConfigureAsync(path);
                _configuredWorkspacePath = result.Workspace.Path;
                WorkspacePath = result.Workspace.Path;
                WorkspaceStatus = FormatWorkspaceStatus(
                    result.Workspace.Path,
                    result.Layout.InboxPath);
                WorkspaceChanged |= changed;
            },
            "工作目录已保存");
    }

    public Task<SettingsOperationResult<ScanRootRegistrationResult>> SaveScanRootAsync(
        ScanRootEditorResult draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return RunOperationAsync(
            async () =>
            {
                ScanRootRegistrationResult result;
                if (draft.Id is null)
                {
                    result = await _services.ScanRoots.AddExternalAsync(
                        draft.Path,
                        draft.FileTypeFilters,
                        draft.ExtensionWhitelist);
                    await _services.ScanRoots.SetIdleScanScheduleAsync(
                        result.Root.Id,
                        draft.IdleScanSchedule);
                    if (result.RequiresInitialScan)
                    {
                        _initialScanRootIds.Add(result.Root.Id);
                        NotifyInitialRootsChanged();
                    }
                }
                else
                {
                    await _services.ScanRoots.SetFileFilterAsync(
                        draft.Id.Value,
                        draft.FileTypeFilters,
                        draft.ExtensionWhitelist);
                    await _services.ScanRoots.SetIdleScanScheduleAsync(
                        draft.Id.Value,
                        draft.IdleScanSchedule);
                    var root = (await _services.ScanRoots.ListExternalAsync())
                        .Single(item => item.Id == draft.Id.Value);
                    result = new ScanRootRegistrationResult(root, [], false);
                }

                await RefreshScanRootsCoreAsync();
                return result;
            },
            draft.Id is null ? "扫描目录已添加" : "扫描设置已保存");
    }

    public Task<SettingsOperationResult> RemoveScanRootAsync(Guid id)
    {
        return RunOperationAsync(
            async () =>
            {
                await _services.ScanRoots.RemoveAsync(id);
                _initialScanRootIds.Remove(id);
                NotifyInitialRootsChanged();
                await RefreshScanRootsCoreAsync();
            },
            "扫描目录已移除");
    }

    public Task<SettingsOperationResult> SaveStorageProfileAsync(
        SaveObjectStorageProfileRequest request)
    {
        return RunOperationAsync(
            async () =>
            {
                await _services.ObjectStorageProfiles.SaveAsync(request);
                await RefreshStorageProfilesCoreAsync();
            },
            "备份配置已保存");
    }

    public Task<SettingsOperationResult> DeleteStorageProfileAsync(Guid id)
    {
        return RunOperationAsync(
            async () =>
            {
                await _services.ObjectStorageProfiles.DeleteAsync(id);
                await RefreshStorageProfilesCoreAsync();
            },
            "备份配置已删除");
    }

    public Task<SettingsOperationResult> SaveOpenWebSourceAsync(
        SaveOpenWebSourceRequest request)
    {
        return RunOperationAsync(
            async () =>
            {
                await _services.OpenWebSettings.SaveAsync(request);
                await RefreshOpenWebSourcesCoreAsync();
            },
            "OpenWeb 源站已保存");
    }

    public Task<SettingsOperationResult> DeleteOpenWebSourceAsync(Guid id)
    {
        return RunOperationAsync(
            async () =>
            {
                await _services.OpenWebSettings.DeleteAsync(id);
                await RefreshOpenWebSourcesCoreAsync();
            },
            "OpenWeb 源站已删除");
    }

    public Task<SettingsOperationResult> SaveGitProfileAsync(SaveGitProfileRequest request)
    {
        return RunOperationAsync(
            async () =>
            {
                await _services.GitProfiles.SaveAsync(request);
                await RefreshGitProfilesCoreAsync();
            },
            "Git 配置已保存");
    }

    public Task<SettingsOperationResult> DeleteGitProfileAsync(Guid id)
    {
        return RunOperationAsync(
            async () =>
            {
                await _services.GitProfiles.DeleteAsync(id);
                await RefreshGitProfilesCoreAsync();
            },
            "Git 配置已删除");
    }

    private async Task ToggleSelectedScanRootAsync()
    {
        var row = SelectedScanRoot;
        if (row is null)
        {
            return;
        }

        await RunOperationAsync(
            async () =>
            {
                var enable = !row.Source.Enabled;
                await _services.ScanRoots.SetEnabledAsync(row.Id, enable);
                if (!enable)
                {
                    _initialScanRootIds.Remove(row.Id);
                }
                else if (row.Source.LastScannedAt is null)
                {
                    _initialScanRootIds.Add(row.Id);
                }

                NotifyInitialRootsChanged();
                await RefreshScanRootsCoreAsync();
            },
            row.Source.Enabled ? "扫描目录已停用" : "扫描目录已启用");
    }

    private async Task SetSelectedOpenWebSourceDefaultAsync()
    {
        var row = SelectedOpenWebSource;
        if (row is null || row.Source.IsDefault)
        {
            return;
        }

        await RunOperationAsync(
            async () =>
            {
                await _services.OpenWebSettings.SetDefaultAsync(row.Id);
                await RefreshOpenWebSourcesCoreAsync();
            },
            "默认 OpenWeb 源站已更新");
    }

    private async Task SetSelectedGitProfileDefaultAsync()
    {
        var row = SelectedGitProfile;
        if (row is null || row.Profile.IsDefault)
        {
            return;
        }

        await RunOperationAsync(
            async () =>
            {
                await _services.GitProfiles.SetDefaultAsync(row.Id);
                await RefreshGitProfilesCoreAsync();
            },
            "默认 Git 配置已更新");
    }

    private async Task RefreshWorkspaceCoreAsync()
    {
        var workspace = await _services.Workspace.GetAsync();
        _configuredWorkspacePath = workspace?.Path;
        WorkspacePath = workspace?.Path ??
            WorkspaceApplicationService.GetSuggestedDefaultPath();
        WorkspaceStatus = workspace is null
            ? "尚未配置"
            : FormatWorkspaceStatus(workspace.Path, workspace.InboxPath);
    }

    private async Task RefreshScanRootsCoreAsync(bool captureInitialRoots = false)
    {
        var selectedId = SelectedScanRoot?.Id;
        var roots = await _services.ScanRoots.ListExternalAsync();
        Replace(ScanRoots, roots.Select(root => new ScanRootSettingsRow(root)));
        SelectedScanRoot = ScanRoots.FirstOrDefault(item => item.Id == selectedId) ??
            ScanRoots.FirstOrDefault();

        if (captureInitialRoots && !_initialRootsCaptured)
        {
            foreach (var root in roots.Where(root => root.Enabled && root.LastScannedAt is null))
            {
                _initialScanRootIds.Add(root.Id);
            }

            _initialRootsCaptured = true;
            NotifyInitialRootsChanged();
        }
    }

    private async Task RefreshStorageProfilesCoreAsync()
    {
        var selectedId = SelectedStorageProfile?.Id;
        var profiles = await _services.ObjectStorageProfiles.ListAsync();
        Replace(
            StorageProfiles,
            profiles.Select(profile => new StorageProfileSettingsRow(profile)));
        SelectedStorageProfile = StorageProfiles.FirstOrDefault(item => item.Id == selectedId) ??
            StorageProfiles.FirstOrDefault();
    }

    private async Task RefreshOpenWebSourcesCoreAsync()
    {
        var selectedId = SelectedOpenWebSource?.Id;
        var sources = await _services.OpenWebSettings.ListAsync();
        Replace(
            OpenWebSources,
            sources.Select(source => new OpenWebSourceSettingsRow(source)));
        SelectedOpenWebSource = OpenWebSources.FirstOrDefault(item => item.Id == selectedId) ??
            OpenWebSources.FirstOrDefault();
    }

    private async Task RefreshGitProfilesCoreAsync()
    {
        var selectedId = SelectedGitProfile?.Id;
        var profiles = await _services.GitProfiles.ListAsync();
        Replace(GitProfiles, profiles.Select(profile => new GitProfileSettingsRow(profile)));
        SelectedGitProfile = GitProfiles.FirstOrDefault(item => item.Id == selectedId) ??
            GitProfiles.FirstOrDefault();
    }

    private async Task<SettingsOperationResult> RunOperationAsync(
        Func<Task> operation,
        string successStatus)
    {
        if (IsBusy)
        {
            return SettingsOperationResult.Failure("另一项设置操作正在进行。");
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await operation();
            StatusText = successStatus;
            return SettingsOperationResult.Success;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusText = "设置操作失败";
            return SettingsOperationResult.Failure(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<SettingsOperationResult<T>> RunOperationAsync<T>(
        Func<Task<T>> operation,
        string successStatus) where T : class
    {
        if (IsBusy)
        {
            return SettingsOperationResult<T>.Failure("另一项设置操作正在进行。");
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var value = await operation();
            StatusText = successStatus;
            return SettingsOperationResult<T>.Success(value);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusText = "设置操作失败";
            return SettingsOperationResult<T>.Failure(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private RelayCommand CreateActionCommand(
        SettingsAction action,
        Func<bool>? canExecute = null,
        Func<object?>? context = null)
    {
        return new RelayCommand(
            () => ActionRequested?.Invoke(
                this,
                new SettingsActionRequestedEventArgs(action, context?.Invoke())),
            () => !IsBusy && (canExecute?.Invoke() ?? true));
    }

    private AsyncRelayCommand CreateAsyncCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null)
    {
        return new AsyncRelayCommand(
            execute,
            canExecute,
            exception =>
            {
                ErrorMessage = exception.Message;
                StatusText = "设置操作失败";
            });
    }

    private void RaiseCommandStates()
    {
        foreach (var command in EnumerateCommands())
        {
            switch (command)
            {
                case RelayCommand relay:
                    relay.RaiseCanExecuteChanged();
                    break;
                case AsyncRelayCommand asyncRelay:
                    asyncRelay.RaiseCanExecuteChanged();
                    break;
            }
        }
    }

    private IEnumerable<ICommand> EnumerateCommands()
    {
        yield return BrowseWorkspaceCommand;
        yield return ApplyWorkspaceCommand;
        yield return AddScanRootCommand;
        yield return EditScanRootCommand;
        yield return ToggleScanRootCommand;
        yield return RemoveScanRootCommand;
        yield return AddStorageProfileCommand;
        yield return EditStorageProfileCommand;
        yield return CopyStorageEndpointCommand;
        yield return CopyStorageBucketCommand;
        yield return DeleteStorageProfileCommand;
        yield return AddOpenWebSourceCommand;
        yield return EditOpenWebSourceCommand;
        yield return OpenOpenWebSourceCommand;
        yield return CopyOpenWebDomainCommand;
        yield return SetDefaultOpenWebSourceCommand;
        yield return DeleteOpenWebSourceCommand;
        yield return AddGitProfileCommand;
        yield return EditGitProfileCommand;
        yield return OpenGitProviderCommand;
        yield return CopyGitRepositoryUrlCommand;
        yield return SetDefaultGitProfileCommand;
        yield return DeleteGitProfileCommand;
        yield return CloseCommand;
        yield return StartInitialScanCommand;
    }

    private void NotifyInitialRootsChanged()
    {
        OnPropertyChanged(nameof(HasInitialScanRoots));
        OnPropertyChanged(nameof(InitialScanRootIds));
        RaiseCommandStates();
    }

    private static string FormatWorkspaceStatus(string workspacePath, string inboxPath)
    {
        return $"Inbox: {inboxPath}{Environment.NewLine}" +
            $"数据库备份: {Path.Combine(workspacePath, "System", "DatabaseBackups")}";
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.Ordinal)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }
    }
}
