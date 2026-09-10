using System.Collections.ObjectModel;
using System.Windows.Input;
using CDSI.Agent.Application.Collections;
using CDSI.Agent.Application.Reader;
using CDSI.Agent.Application.Scanning;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Fingerprints;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Reader;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Core.Transfers;
using CDSI.Agent.Mac.Services;

namespace CDSI.Agent.Mac.ViewModels;

public enum MainViewAction
{
    CreateProject,
    OpenWorkspace,
    OpenDataDirectory,
    OpenSettings,
    ShowDataProtection,
    ShowTaskCenter,
    ShowRuntimeLog,
    CheckForUpdates,
    OpenDocumentation,
    ShowPrivacy,
    ShowLicense,
    ShowThirdPartyNotices,
    ShowAbout,
    OpenAssetLocation,
    OpenAssetProject,
    ShowAssetDetails,
    ManageAssetTags,
    AddAssetsToProject,
    PublishAsset,
    CopyAssetsToWorkspace,
    MoveAssetsToWorkspace,
    BackupAssets,
    RestoreAssets,
    RemoveAssets,
    RemoveAssetDirectory,
    OpenDuplicateLocation,
    ShowDuplicateDetails,
    EditProject,
    DeleteProject,
    SyncProjectToGit,
    OpenProjectAssetLocation,
    RemoveProjectAssets,
    DeleteCloudBackupProject,
    OpenCloudBackupLocation,
    RestoreCloudBackup,
    DeleteCloudBackup,
    OpenGitProject,
    OpenGitRepository,
    CopyGitRepositoryUrl,
    AddReaderFeed,
    RefreshSelectedReaderFeed,
    OpenReaderFeedSite,
    RemoveReaderFeed,
    ImportReaderOpml,
    ExportReaderOpml,
    ImportReaderData,
    ExportReaderData,
    OpenReaderEntry,
    ToggleReaderEntryRead,
    ToggleReaderEntryStar,
    OpenAssetDirectory,
    SyncProject,
    SyncGitProject
}

public sealed class MainViewActionRequestedEventArgs(
    MainViewAction action,
    object? context = null) : EventArgs
{
    public MainViewAction Action { get; } = action;

    public object? Context { get; } = context;
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int ReaderTabIndex = 6;
    private const string ReaderAllNavigationKey = "all";

    private static readonly IReadOnlyList<AssetFileTypeOption> AssetFileTypes =
    [
        new("全部类型", AssetFileTypeFilter.All),
        new("视频", AssetFileTypeFilter.Video),
        new("音频", AssetFileTypeFilter.Audio),
        new("图片", AssetFileTypeFilter.Image),
        new("文档", AssetFileTypeFilter.Document),
        new("其他", AssetFileTypeFilter.Other)
    ];

    private readonly AppServices _services;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly List<ManagedObjectStorageBackup> _allCloudBackups = [];
    private readonly List<GitProjectRowViewModel> _allGitProjects = [];
    private CancellationTokenSource? _operationCancellation;
    private bool _disposed;
    private bool _initialized;
    private bool _canCancelCurrentTask;
    private bool _suppressSelectionRefresh;
    private bool _suppressReaderSelectionActions;
    private long _projectAssetRefreshVersion;
    private AssetListFilter _appliedAssetFilter = AssetListFilter.Empty;
    private long _assetPageIndex;
    private long _assetTotalItems;
    private long _unfilteredAssetTotalItems;
    private int _selectedTabIndex;
    private bool _isBusy;
    private bool _isProgressIndeterminate;
    private double _progressValue;
    private string _progressText = "就绪";
    private string _currentPath = "尚未扫描";
    private string _statusText = "正在初始化";
    private string? _lastError;
    private string? _workspacePath;
    private AssetRowViewModel? _selectedAsset;
    private string _assetSearchText = string.Empty;
    private AssetFileTypeOption _selectedFileType = AssetFileTypes[0];
    private AssetExtensionOption _selectedExtension;
    private AssetTagOption _selectedTag;
    private DateTimeOffset? _createdFrom;
    private DateTimeOffset? _createdTo;
    private string _assetResultText = "全部 0";
    private int _selectedPageSize = 100;
    private string _assetPageText = "第 1 / 1 页 · 0 条";
    private AssetDirectoryRowViewModel? _selectedAssetDirectory;
    private string _assetDirectorySummaryText = "尚未加载";
    private int _duplicateGroupCount;
    private DuplicateAssetRowViewModel? _selectedDuplicateAsset;
    private ProjectRowViewModel? _selectedProject;
    private ProjectAssetRowViewModel? _selectedProjectAsset;
    private CloudBackupProjectRowViewModel? _selectedCloudBackupProject;
    private CloudBackupRowViewModel? _selectedCloudBackup;
    private string _cloudBackupSearchText = string.Empty;
    private string _cloudBackupSummaryText = "尚未加载";
    private GitProjectRowViewModel? _selectedGitProject;
    private string _gitProjectSearchText = string.Empty;
    private string _gitProjectSummaryText = "尚未加载";
    private ReaderNavigationRowViewModel? _selectedReaderNavigation;
    private ReaderEntryRowViewModel? _selectedReaderEntry;
    private Guid? _pendingReaderAutoReadEntryId;
    private bool _readerAutoReadOnActivationPending;
    private string _readerSearchText = string.Empty;
    private string _readerSummaryText = "尚未加载";
    private StatisticsViewModel _statistics = StatisticsViewModel.Empty;

    public MainViewModel(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _selectedExtension = new AssetExtensionOption("全部扩展名", null);
        _selectedTag = new AssetTagOption("全部标签", null);

        FileTypeOptions = AssetFileTypes;
        PageSizeOptions = [100, 200, 500];
        ExtensionOptions.Add(_selectedExtension);
        TagOptions.Add(_selectedTag);

        StartStandardScanCommand = CreateAsyncCommand(
            () => RunScanPipelineAsync(FingerprintMode.DuplicateCandidates),
            () => !IsBusy);
        StartFullScanCommand = CreateAsyncCommand(
            () => RunScanPipelineAsync(FingerprintMode.Complete),
            () => !IsBusy);
        CancelCurrentTaskCommand = new RelayCommand(
            CancelCurrentTask,
            () => IsBusy && _canCancelCurrentTask);
        RefreshCommand = CreateAsyncCommand(
            RefreshWithStatusAsync,
            () => !IsBusy);
        CreateProjectCommand = CreateActionCommand(
            MainViewAction.CreateProject,
            () => !IsBusy);
        OpenWorkspaceCommand = CreateActionCommand(
            MainViewAction.OpenWorkspace,
            () => !IsBusy && WorkspacePath is not null,
            () => WorkspacePath);
        OpenDataDirectoryCommand = CreateActionCommand(
            MainViewAction.OpenDataDirectory,
            () => !IsBusy,
            () => _services.DataDirectory);
        OpenSettingsCommand = CreateActionCommand(
            MainViewAction.OpenSettings,
            () => !IsBusy);
        ShowDataProtectionCommand = CreateActionCommand(
            MainViewAction.ShowDataProtection,
            () => !IsBusy);
        ShowTaskCenterCommand = CreateActionCommand(MainViewAction.ShowTaskCenter);
        ShowRuntimeLogCommand = CreateActionCommand(MainViewAction.ShowRuntimeLog);
        CheckForUpdatesCommand = CreateActionCommand(
            MainViewAction.CheckForUpdates,
            () => !IsBusy);
        OpenDocumentationCommand = CreateActionCommand(MainViewAction.OpenDocumentation);
        ShowPrivacyCommand = CreateActionCommand(MainViewAction.ShowPrivacy);
        ShowLicenseCommand = CreateActionCommand(MainViewAction.ShowLicense);
        ShowThirdPartyNoticesCommand = CreateActionCommand(
            MainViewAction.ShowThirdPartyNotices);
        ShowAboutCommand = CreateActionCommand(MainViewAction.ShowAbout);
        OpenAssetLocationCommand = AssetAction(MainViewAction.OpenAssetLocation);
        OpenAssetProjectCommand = CreateActionCommand(
            MainViewAction.OpenAssetProject,
            () => !IsBusy && SelectedAsset?.Source.ProjectNames.Count > 0,
            () => SelectedAsset);
        ShowAssetDetailsCommand = AssetAction(MainViewAction.ShowAssetDetails);
        ManageAssetTagsCommand = AssetAction(MainViewAction.ManageAssetTags);
        AddAssetsToProjectCommand = AssetAction(MainViewAction.AddAssetsToProject);
        PublishAssetCommand = AssetAction(MainViewAction.PublishAsset);
        CopyAssetsToWorkspaceCommand = AssetAction(MainViewAction.CopyAssetsToWorkspace);
        MoveAssetsToWorkspaceCommand = AssetAction(MainViewAction.MoveAssetsToWorkspace);
        BackupAssetsCommand = AssetAction(MainViewAction.BackupAssets);
        RestoreAssetsCommand = AssetAction(MainViewAction.RestoreAssets);
        RemoveAssetsCommand = AssetAction(MainViewAction.RemoveAssets);
        SearchAssetsCommand = CreateAsyncCommand(
            ApplyAssetFilterAsync,
            () => !IsBusy);
        ResetAssetFiltersCommand = CreateAsyncCommand(
            ResetAssetFiltersAsync,
            () => !IsBusy);
        PreviousAssetPageCommand = CreateAsyncCommand(
            () => NavigateAssetPageAsync(-1),
            () => !IsBusy && CanPreviousAssetPage);
        NextAssetPageCommand = CreateAsyncCommand(
            () => NavigateAssetPageAsync(1),
            () => !IsBusy && CanNextAssetPage);
        OpenAssetDirectoryCommand = CreateActionCommand(
            MainViewAction.OpenAssetDirectory,
            () => !IsBusy && SelectedAssetDirectory is not null,
            () => SelectedAssetDirectory);
        RemoveAssetDirectoryCommand = CreateActionCommand(
            MainViewAction.RemoveAssetDirectory,
            () => !IsBusy && SelectedAssetDirectory is not null,
            () => SelectedAssetDirectory);
        OpenDuplicateLocationCommand = CreateActionCommand(
            MainViewAction.OpenDuplicateLocation,
            () => !IsBusy && SelectedDuplicateAsset is not null,
            () => SelectedDuplicateAsset);
        ShowDuplicateDetailsCommand = CreateActionCommand(
            MainViewAction.ShowDuplicateDetails,
            () => !IsBusy && SelectedDuplicateAsset is not null,
            () => SelectedDuplicateAsset);
        SyncProjectCommand = CreateActionCommand(
            MainViewAction.SyncProject,
            () => !IsBusy && SelectedProject is not null,
            () => SelectedProject);
        EditProjectCommand = ProjectAction(MainViewAction.EditProject);
        DeleteProjectCommand = ProjectAction(MainViewAction.DeleteProject);
        SyncProjectToGitCommand = ProjectAction(MainViewAction.SyncProjectToGit);
        OpenProjectAssetLocationCommand = CreateActionCommand(
            MainViewAction.OpenProjectAssetLocation,
            () => !IsBusy && SelectedProjectAsset is not null,
            () => SelectedProjectAsset);
        RemoveProjectAssetsCommand = CreateActionCommand(
            MainViewAction.RemoveProjectAssets,
            () => !IsBusy && SelectedProject is not null && SelectedProjectAsset is not null,
            () => SelectedProjectAsset);
        SearchCloudBackupsCommand = new RelayCommand(
            ApplyCloudBackupSearch,
            () => !IsBusy);
        RefreshCloudBackupsCommand = CreateAsyncCommand(
            RefreshCloudBackupsWithStatusAsync,
            () => !IsBusy);
        DeleteCloudBackupProjectCommand = CloudProjectAction(
            MainViewAction.DeleteCloudBackupProject);
        OpenCloudBackupLocationCommand = CloudBackupAction(
            MainViewAction.OpenCloudBackupLocation);
        RestoreCloudBackupCommand = CloudBackupAction(MainViewAction.RestoreCloudBackup);
        DeleteCloudBackupCommand = CloudBackupAction(MainViewAction.DeleteCloudBackup);
        SearchGitProjectsCommand = new RelayCommand(
            ApplyGitProjectSearch,
            () => !IsBusy);
        ResetGitProjectSearchCommand = new RelayCommand(
            ResetGitProjectSearch,
            () => !IsBusy);
        SyncGitProjectCommand = CreateActionCommand(
            MainViewAction.SyncGitProject,
            () => !IsBusy && SelectedGitProject is
                { LocalProjectAvailable: true, GitProfileAvailable: true },
            () => SelectedGitProject);
        OpenGitProjectCommand = GitAction(MainViewAction.OpenGitProject);
        OpenGitRepositoryCommand = GitAction(MainViewAction.OpenGitRepository);
        CopyGitRepositoryUrlCommand = GitAction(MainViewAction.CopyGitRepositoryUrl);
        AddReaderFeedCommand = CreateActionCommand(
            MainViewAction.AddReaderFeed,
            () => !IsBusy && _services.ReaderAvailable);
        RefreshReaderFeedsCommand = CreateAsyncCommand(
            RefreshReaderFeedsFromNetworkAsync,
            () => !IsBusy && _services.ReaderAvailable);
        SearchReaderEntriesCommand = CreateAsyncCommand(
            SearchReaderEntriesWithStatusAsync,
            () => !IsBusy && _services.ReaderAvailable);
        RefreshSelectedReaderFeedCommand = ReaderFeedAction(
            MainViewAction.RefreshSelectedReaderFeed);
        OpenReaderFeedSiteCommand = CreateActionCommand(
            MainViewAction.OpenReaderFeedSite,
            () => !IsBusy &&
                _services.ReaderAvailable &&
                !string.IsNullOrWhiteSpace(SelectedReaderFeed?.Source.Feed.SiteUrl),
            () => SelectedReaderFeed);
        RemoveReaderFeedCommand = ReaderFeedAction(MainViewAction.RemoveReaderFeed);
        ImportReaderOpmlCommand = CreateActionCommand(
            MainViewAction.ImportReaderOpml,
            () => !IsBusy && _services.ReaderAvailable);
        ExportReaderOpmlCommand = CreateActionCommand(
            MainViewAction.ExportReaderOpml,
            () => !IsBusy && _services.ReaderAvailable);
        ImportReaderDataCommand = CreateActionCommand(
            MainViewAction.ImportReaderData,
            () => !IsBusy && _services.ReaderAvailable);
        ExportReaderDataCommand = CreateActionCommand(
            MainViewAction.ExportReaderData,
            () => !IsBusy && _services.ReaderAvailable);
        OpenReaderEntryCommand = ReaderEntryAction(MainViewAction.OpenReaderEntry);
        ToggleReaderEntryReadCommand = ReaderEntryAction(
            MainViewAction.ToggleReaderEntryRead);
        ToggleReaderEntryStarCommand = ReaderEntryAction(
            MainViewAction.ToggleReaderEntryStar);

        RelayCommand AssetAction(MainViewAction action) => CreateActionCommand(
            action,
            () => !IsBusy && SelectedAsset is not null,
            () => SelectedAsset);
        RelayCommand ProjectAction(MainViewAction action) => CreateActionCommand(
            action,
            () => !IsBusy && SelectedProject is not null,
            () => SelectedProject);
        RelayCommand CloudProjectAction(MainViewAction action) => CreateActionCommand(
            action,
            () => !IsBusy && SelectedCloudBackupProject is not null,
            () => SelectedCloudBackupProject);
        RelayCommand CloudBackupAction(MainViewAction action) => CreateActionCommand(
            action,
            () => !IsBusy && SelectedCloudBackup is not null,
            () => SelectedCloudBackup);
        RelayCommand GitAction(MainViewAction action) => CreateActionCommand(
            action,
            () => !IsBusy && SelectedGitProject is not null,
            () => SelectedGitProject);
        RelayCommand ReaderFeedAction(MainViewAction action) => CreateActionCommand(
            action,
            () => !IsBusy && _services.ReaderAvailable && SelectedReaderFeed is not null,
            () => SelectedReaderFeed);
        RelayCommand ReaderEntryAction(MainViewAction action) => CreateActionCommand(
            action,
            () => !IsBusy && _services.ReaderAvailable && SelectedReaderEntry is not null,
            () => SelectedReaderEntry);
    }

    public event EventHandler<MainViewActionRequestedEventArgs>? UiActionRequested;

    public AppServices Services => _services;

    public string ApplicationVersion => _services.ApplicationVersion;

    public string DataDirectoryText => $"数据目录: {_services.DataDirectory}";

    public string? WorkspacePath
    {
        get => _workspacePath;
        private set
        {
            if (SetProperty(ref _workspacePath, value))
            {
                OnPropertyChanged(nameof(RequiresWorkspaceSetup));
                RaiseCommandStates();
            }
        }
    }

    public bool RequiresWorkspaceSetup => WorkspacePath is null;

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (!SetProperty(ref _selectedTabIndex, Math.Clamp(value, 0, 7)))
            {
                return;
            }

            if (_selectedTabIndex == ReaderTabIndex)
            {
                _readerAutoReadOnActivationPending = true;
                QueueSelectedReaderEntryAutoRead();
            }
            else
            {
                _readerAutoReadOnActivationPending = false;
                _pendingReaderAutoReadEntryId = null;
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsProgressVisible));
                RaiseCommandStates();
            }
        }
    }

    public bool IsProgressVisible => IsBusy;

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, Math.Clamp(value, 0d, 100d));
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public ObservableCollection<AssetRowViewModel> Assets { get; } = [];

    public AssetRowViewModel? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (SetProperty(ref _selectedAsset, value))
            {
                OnPropertyChanged(nameof(SelectedAssetTitle));
                OnPropertyChanged(nameof(SelectedAssetSummary));
                RaiseCommandStates();
            }
        }
    }

    public string SelectedAssetTitle => SelectedAsset?.OriginalFilename ?? "未选择资产";

    public string SelectedAssetSummary => SelectedAsset is null
        ? string.Empty
        : string.Join(
            "  |  ",
            $"{SelectedAsset.TypeText} · {SelectedAsset.SizeText} · 修改 {SelectedAsset.ModifiedAtText} · 索引 {SelectedAsset.IndexedAtText}",
            $"标签：{SelectedAsset.TagsText}",
            $"备份：{SelectedAsset.BackupStatusText}",
            SelectedAsset.Path);

    public string AssetSearchText
    {
        get => _assetSearchText;
        set => SetProperty(ref _assetSearchText, value ?? string.Empty);
    }

    public IReadOnlyList<AssetFileTypeOption> FileTypeOptions { get; }

    public AssetFileTypeOption SelectedFileType
    {
        get => _selectedFileType;
        set
        {
            if (value is not null && SetProperty(ref _selectedFileType, value) &&
                _initialized && !IsBusy)
            {
                _ = RefreshExtensionOptionsGuardedAsync();
            }
        }
    }

    public ObservableCollection<AssetExtensionOption> ExtensionOptions { get; } = [];

    public AssetExtensionOption SelectedExtension
    {
        get => _selectedExtension;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedExtension, value);
            }
        }
    }

    public ObservableCollection<AssetTagOption> TagOptions { get; } = [];

    public AssetTagOption SelectedTag
    {
        get => _selectedTag;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedTag, value);
            }
        }
    }

    public DateTimeOffset? CreatedFrom
    {
        get => _createdFrom;
        set => SetProperty(ref _createdFrom, value);
    }

    public DateTimeOffset? CreatedTo
    {
        get => _createdTo;
        set => SetProperty(ref _createdTo, value);
    }

    public string AssetResultText
    {
        get => _assetResultText;
        private set => SetProperty(ref _assetResultText, value);
    }

    public IReadOnlyList<int> PageSizeOptions { get; }

    public int SelectedPageSize
    {
        get => _selectedPageSize;
        set
        {
            if (!PageSizeOptions.Contains(value) ||
                !SetProperty(ref _selectedPageSize, value))
            {
                return;
            }

            _assetPageIndex = 0;
            if (_initialized && !IsBusy)
            {
                _ = RefreshAssetPageGuardedAsync();
            }
        }
    }

    public string AssetPageText
    {
        get => _assetPageText;
        private set => SetProperty(ref _assetPageText, value);
    }

    public bool CanPreviousAssetPage => _assetPageIndex > 0;

    public bool CanNextAssetPage =>
        _assetPageIndex + 1 < CalculatePageCount(_assetTotalItems, SelectedPageSize);

    public ObservableCollection<AssetDirectoryRowViewModel> AssetDirectories { get; } = [];

    public AssetDirectoryRowViewModel? SelectedAssetDirectory
    {
        get => _selectedAssetDirectory;
        set
        {
            if (SetProperty(ref _selectedAssetDirectory, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string AssetDirectorySummaryText
    {
        get => _assetDirectorySummaryText;
        private set => SetProperty(ref _assetDirectorySummaryText, value);
    }

    public ObservableCollection<DuplicateAssetRowViewModel> DuplicateAssets { get; } = [];

    public DuplicateAssetRowViewModel? SelectedDuplicateAsset
    {
        get => _selectedDuplicateAsset;
        set => SetProperty(ref _selectedDuplicateAsset, value);
    }

    public string DuplicateSummaryText =>
        $"{_duplicateGroupCount:N0} 个重复组 · {DuplicateAssets.Count:N0} 个文件";

    public ObservableCollection<ProjectRowViewModel> Projects { get; } = [];

    public string ProjectSummaryText => $"{Projects.Count:N0} 个项目";

    public ProjectRowViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value))
            {
                return;
            }

            var refreshVersion = Interlocked.Increment(ref _projectAssetRefreshVersion);
            ProjectAssets.Clear();
            SelectedProjectAsset = null;
            RaiseCommandStates();
            if (!_suppressSelectionRefresh && _initialized)
            {
                _ = RefreshSelectedProjectAssetsGuardedAsync(value, refreshVersion);
            }
        }
    }

    public ObservableCollection<ProjectAssetRowViewModel> ProjectAssets { get; } = [];

    public ProjectAssetRowViewModel? SelectedProjectAsset
    {
        get => _selectedProjectAsset;
        set => SetProperty(ref _selectedProjectAsset, value);
    }

    public ObservableCollection<CloudBackupProjectRowViewModel> CloudBackupProjects { get; } = [];

    public CloudBackupProjectRowViewModel? SelectedCloudBackupProject
    {
        get => _selectedCloudBackupProject;
        set
        {
            if (SetProperty(ref _selectedCloudBackupProject, value))
            {
                PopulateSelectedCloudBackupProject();
            }
        }
    }

    public ObservableCollection<CloudBackupRowViewModel> CloudBackups { get; } = [];

    public CloudBackupRowViewModel? SelectedCloudBackup
    {
        get => _selectedCloudBackup;
        set => SetProperty(ref _selectedCloudBackup, value);
    }

    public string CloudBackupSearchText
    {
        get => _cloudBackupSearchText;
        set => SetProperty(ref _cloudBackupSearchText, value ?? string.Empty);
    }

    public string CloudBackupSummaryText
    {
        get => _cloudBackupSummaryText;
        private set => SetProperty(ref _cloudBackupSummaryText, value);
    }

    public ObservableCollection<GitProjectRowViewModel> GitProjects { get; } = [];

    public GitProjectRowViewModel? SelectedGitProject
    {
        get => _selectedGitProject;
        set
        {
            if (SetProperty(ref _selectedGitProject, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string GitProjectSearchText
    {
        get => _gitProjectSearchText;
        set => SetProperty(ref _gitProjectSearchText, value ?? string.Empty);
    }

    public string GitProjectSummaryText
    {
        get => _gitProjectSummaryText;
        private set => SetProperty(ref _gitProjectSummaryText, value);
    }

    public ObservableCollection<ReaderFeedRowViewModel> ReaderFeeds { get; } = [];

    public ObservableCollection<ReaderNavigationRowViewModel> ReaderNavigationItems { get; } = [];

    public ReaderNavigationRowViewModel? SelectedReaderNavigation
    {
        get => _selectedReaderNavigation;
        set
        {
            if (!SetProperty(ref _selectedReaderNavigation, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedReaderFeed));
            RaiseCommandStates();
            if (!_suppressReaderSelectionActions && _initialized)
            {
                _ = RefreshReaderEntriesGuardedAsync();
            }
        }
    }

    public ReaderFeedRowViewModel? SelectedReaderFeed => SelectedReaderNavigation?.Feed;

    public ObservableCollection<ReaderEntryRowViewModel> ReaderEntries { get; } = [];

    public ReaderEntryRowViewModel? SelectedReaderEntry
    {
        get => _selectedReaderEntry;
        set
        {
            if (!SetProperty(ref _selectedReaderEntry, value))
            {
                return;
            }

            RaiseCommandStates();
            if (!_suppressReaderSelectionActions)
            {
                QueueSelectedReaderEntryAutoRead();
            }
        }
    }

    public string ReaderSearchText
    {
        get => _readerSearchText;
        set => SetProperty(ref _readerSearchText, value ?? string.Empty);
    }

    public string ReaderSummaryText
    {
        get => _readerSummaryText;
        private set => SetProperty(ref _readerSummaryText, value);
    }

    public StatisticsViewModel Statistics
    {
        get => _statistics;
        private set => SetProperty(ref _statistics, value);
    }

    public ICommand StartStandardScanCommand { get; }

    public ICommand StartFullScanCommand { get; }

    public ICommand CancelCurrentTaskCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand CreateProjectCommand { get; }

    public ICommand OpenWorkspaceCommand { get; }

    public ICommand OpenDataDirectoryCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand ShowDataProtectionCommand { get; }

    public ICommand ShowTaskCenterCommand { get; }

    public ICommand ShowRuntimeLogCommand { get; }

    public ICommand CheckForUpdatesCommand { get; }

    public ICommand OpenDocumentationCommand { get; }

    public ICommand ShowPrivacyCommand { get; }

    public ICommand ShowLicenseCommand { get; }

    public ICommand ShowThirdPartyNoticesCommand { get; }

    public ICommand ShowAboutCommand { get; }

    public ICommand OpenAssetLocationCommand { get; }

    public ICommand OpenAssetProjectCommand { get; }

    public ICommand ShowAssetDetailsCommand { get; }

    public ICommand ManageAssetTagsCommand { get; }

    public ICommand AddAssetsToProjectCommand { get; }

    public ICommand PublishAssetCommand { get; }

    public ICommand CopyAssetsToWorkspaceCommand { get; }

    public ICommand MoveAssetsToWorkspaceCommand { get; }

    public ICommand BackupAssetsCommand { get; }

    public ICommand RestoreAssetsCommand { get; }

    public ICommand RemoveAssetsCommand { get; }

    public ICommand SearchAssetsCommand { get; }

    public ICommand ResetAssetFiltersCommand { get; }

    public ICommand PreviousAssetPageCommand { get; }

    public ICommand NextAssetPageCommand { get; }

    public ICommand OpenAssetDirectoryCommand { get; }

    public ICommand RemoveAssetDirectoryCommand { get; }

    public ICommand OpenDuplicateLocationCommand { get; }

    public ICommand ShowDuplicateDetailsCommand { get; }

    public ICommand SyncProjectCommand { get; }

    public ICommand EditProjectCommand { get; }

    public ICommand DeleteProjectCommand { get; }

    public ICommand SyncProjectToGitCommand { get; }

    public ICommand OpenProjectAssetLocationCommand { get; }

    public ICommand RemoveProjectAssetsCommand { get; }

    public ICommand SearchCloudBackupsCommand { get; }

    public ICommand RefreshCloudBackupsCommand { get; }

    public ICommand DeleteCloudBackupProjectCommand { get; }

    public ICommand OpenCloudBackupLocationCommand { get; }

    public ICommand RestoreCloudBackupCommand { get; }

    public ICommand DeleteCloudBackupCommand { get; }

    public ICommand SearchGitProjectsCommand { get; }

    public ICommand ResetGitProjectSearchCommand { get; }

    public ICommand SyncGitProjectCommand { get; }

    public ICommand OpenGitProjectCommand { get; }

    public ICommand OpenGitRepositoryCommand { get; }

    public ICommand CopyGitRepositoryUrlCommand { get; }

    public ICommand AddReaderFeedCommand { get; }

    public ICommand RefreshReaderFeedsCommand { get; }

    public ICommand SearchReaderEntriesCommand { get; }

    public ICommand RefreshSelectedReaderFeedCommand { get; }

    public ICommand OpenReaderFeedSiteCommand { get; }

    public ICommand RemoveReaderFeedCommand { get; }

    public ICommand ImportReaderOpmlCommand { get; }

    public ICommand ExportReaderOpmlCommand { get; }

    public ICommand ImportReaderDataCommand { get; }

    public ICommand ExportReaderDataCommand { get; }

    public ICommand OpenReaderEntryCommand { get; }

    public ICommand ToggleReaderEntryReadCommand { get; }

    public ICommand ToggleReaderEntryStarCommand { get; }

    public Task InitializeAsync()
    {
        return RunOperationAsync(
            "正在初始化",
            allowCancel: false,
            operation: async cancellationToken =>
            {
                var restore = await _services.PendingStateRestore.ApplyPendingAsync(
                    cancellationToken);
                await _services.InitializeAsync(cancellationToken);
                var workspace = await _services.Workspace.GetAsync(cancellationToken);
                WorkspacePath = workspace?.Path;

                try
                {
                    await _services.LocalVolumes.ReconcileAsync(cancellationToken);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException)
                {
                    LastError = $"本地卷状态暂时无法刷新：{exception.Message}";
                }

                await RefreshAllPagesAsync(cancellationToken);
                _initialized = true;
                StatusText = !_services.ReaderAvailable
                    ? $"资产索引已就绪；RSS 功能不可用：{_services.ReaderInitializationError}"
                    : restore is not null
                    ? "状态恢复完成，应用已就绪"
                    : workspace is null
                        ? "索引已就绪，请先配置 CDSI 工作目录"
                        : "就绪";
            },
            rethrowErrors: true);
    }

    public async Task RefreshAllPagesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tasks = new List<Task>
        {
            RefreshAssetsAsync(cancellationToken),
            RefreshAssetDirectoriesAsync(cancellationToken),
            RefreshDuplicatesAsync(cancellationToken),
            RefreshProjectsAsync(cancellationToken),
            RefreshCloudBackupsAsync(cancellationToken),
            RefreshGitProjectsAsync(cancellationToken),
            RefreshStatisticsAsync(cancellationToken)
        };
        if (_services.ReaderAvailable)
        {
            tasks.Add(RefreshReaderAsync(cancellationToken));
        }
        else
        {
            ReaderFeeds.Clear();
            ReaderNavigationItems.Clear();
            SelectedReaderNavigation = null;
            ReaderEntries.Clear();
            SelectedReaderEntry = null;
            ReaderSummaryText = $"RSS 功能不可用：{_services.ReaderInitializationError}";
        }

        await Task.WhenAll(tasks);
    }

    public async Task RefreshAssetsAsync(CancellationToken cancellationToken = default)
    {
        var filter = _appliedAssetFilter;
        var filteredCountTask = _services.Scan.GetAssetListCountAsync(
            filter,
            cancellationToken);
        var totalCountTask = filter.IsEmpty
            ? filteredCountTask
            : _services.Scan.GetAssetListCountAsync(cancellationToken);
        var extensionsTask = _services.Scan.ListAssetExtensionsAsync(
            SelectedFileType.Value,
            cancellationToken);
        var tagsTask = _services.AssetTags.ListAsync(cancellationToken);
        await Task.WhenAll(filteredCountTask, totalCountTask, extensionsTask, tagsTask);

        _assetTotalItems = await filteredCountTask;
        _unfilteredAssetTotalItems = await totalCountTask;
        var pageCount = CalculatePageCount(_assetTotalItems, SelectedPageSize);
        _assetPageIndex = Math.Clamp(_assetPageIndex, 0, pageCount - 1);
        var offset = _assetPageIndex * SelectedPageSize;
        var items = await _services.Scan.ListAssetsAsync(
            filter,
            SelectedPageSize,
            offset,
            cancellationToken);

        var selectedId = SelectedAsset?.Source.AssetId;
        Replace(
            Assets,
            items.Select((item, index) => new AssetRowViewModel
            {
                Source = item,
                RowNumber = offset + index + 1
            }));
        SelectedAsset = Assets.FirstOrDefault(item => item.Source.AssetId == selectedId) ??
            Assets.FirstOrDefault();
        UpdateAssetFilterOptions(await extensionsTask, await tagsTask);
        UpdateAssetPaginationText();
    }

    public async Task RefreshAssetDirectoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var selectedPath = SelectedAssetDirectory?.Path;
        var directories = await _services.Scan.ListAssetDirectoriesAsync(cancellationToken);
        Replace(
            AssetDirectories,
            directories.Select(item => new AssetDirectoryRowViewModel { Source = item }));
        SelectedAssetDirectory = AssetDirectories.FirstOrDefault(
            item => string.Equals(item.Path, selectedPath, StringComparison.Ordinal)) ??
            AssetDirectories.FirstOrDefault();
        AssetDirectorySummaryText =
            $"{directories.Count:N0} 个目录 · " +
            $"{directories.Sum(item => item.AvailableAssetCount):N0} 个可用资源";
    }

    public async Task RefreshDuplicatesAsync(CancellationToken cancellationToken = default)
    {
        var groups = await _services.Scan.ListExactDuplicateGroupsAsync(
            cancellationToken: cancellationToken);
        _duplicateGroupCount = groups.Count;
        Replace(DuplicateAssets, DuplicateAssetRowViewModel.Create(groups));
        SelectedDuplicateAsset = DuplicateAssets.FirstOrDefault();
        OnPropertyChanged(nameof(DuplicateSummaryText));
    }

    public async Task RefreshProjectsAsync(CancellationToken cancellationToken = default)
    {
        var selectedId = SelectedProject?.Id;
        var projects = await _services.AssetCollections.ListAsync(cancellationToken);
        Replace(
            Projects,
            projects.Select(item => new ProjectRowViewModel { Source = item }));
        OnPropertyChanged(nameof(ProjectSummaryText));

        _suppressSelectionRefresh = true;
        try
        {
            SelectedProject = Projects.FirstOrDefault(item => item.Id == selectedId) ??
                Projects.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionRefresh = false;
        }

        await RefreshSelectedProjectAssetsAsync(cancellationToken);
    }

    public async Task RefreshCloudBackupsAsync(
        CancellationToken cancellationToken = default)
    {
        var backups = await _services.ObjectStorageManagement.ListAsync(cancellationToken);
        _allCloudBackups.Clear();
        _allCloudBackups.AddRange(backups);
        ApplyCloudBackupSearch();
    }

    public async Task RefreshGitProjectsAsync(CancellationToken cancellationToken = default)
    {
        var recordsTask = _services.GitProjectSync.ListAsync(cancellationToken);
        var projectsTask = _services.AssetCollections.ListAsync(cancellationToken);
        var profilesTask = _services.GitProfiles.ListAsync(cancellationToken);
        await Task.WhenAll(recordsTask, projectsTask, profilesTask);

        var projects = (await projectsTask).ToDictionary(project => project.Id);
        var profileIds = (await profilesTask)
            .Select(profile => profile.Profile.Id)
            .ToHashSet();
        _allGitProjects.Clear();
        foreach (var record in await recordsTask)
        {
            var projectAvailable = projects.TryGetValue(record.ProjectId, out var project);
            var profileAvailable = profileIds.Contains(record.ProfileId);
            _allGitProjects.Add(new GitProjectRowViewModel
            {
                Record = record,
                ProjectName = project?.Name ?? record.ProjectName,
                ProjectType = project?.Type ?? record.ProjectType,
                LocalProjectAvailable = projectAvailable,
                GitProfileAvailable = profileAvailable,
                LocalStateText = (projectAvailable, profileAvailable) switch
                {
                    (true, true) => "可用",
                    (false, true) => "项目已删除",
                    (true, false) => "Git配置已删除",
                    _ => "项目、配置已删除"
                }
            });
        }

        ApplyGitProjectSearch();
    }

    public async Task RefreshReaderAsync(CancellationToken cancellationToken = default)
    {
        var selectedNavigationKey = SelectedReaderNavigation?.NavigationKey ??
            ReaderAllNavigationKey;
        var feeds = await _services.Reader.ListFeedsAsync(cancellationToken);
        PopulateReaderNavigation(feeds, selectedNavigationKey);
        await RefreshReaderEntriesAsync(cancellationToken);
    }

    public async Task RefreshStatisticsAsync(CancellationToken cancellationToken = default)
    {
        Statistics = StatisticsViewModel.From(
            await _services.Scan.GetLocalAssetStatisticsAsync(cancellationToken));
    }

    public async Task ConfigureWorkspaceAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var result = await _services.Workspace.ConfigureAsync(path, cancellationToken);
        WorkspacePath = result.Workspace.Path;
        await RefreshAllPagesAsync(cancellationToken);
        StatusText = $"工作目录已配置：{result.Workspace.Path}";
    }

    public async Task ReloadConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = await _services.Workspace.GetAsync(cancellationToken);
        WorkspacePath = workspace?.Path;
        await RefreshAllPagesAsync(cancellationToken);
        StatusText = workspace is null ? "尚未配置 CDSI 工作目录" : "设置已应用";
    }

    public async Task<ScanRootRegistrationResult> AddScanRootAsync(
        string path,
        IReadOnlyCollection<AssetFileTypeFilter>? fileTypes = null,
        IReadOnlyCollection<string>? extensions = null,
        CancellationToken cancellationToken = default)
    {
        return await _services.ScanRoots.AddExternalAsync(
            path,
            fileTypes ?? [AssetFileTypeFilter.All],
            extensions ?? [],
            cancellationToken);
    }

    public async Task<ProjectRowViewModel> CreateProjectAsync(
        string name,
        AssetCollectionType type,
        IReadOnlyCollection<Guid>? backupProfileIds = null,
        CancellationToken cancellationToken = default)
    {
        var created = await _services.AssetCollections.CreateAsync(
            name,
            type,
            backupProfileIds,
            cancellationToken);
        await RefreshProjectsAsync(cancellationToken);
        SelectedProject = Projects.First(item => item.Id == created.Id);
        return SelectedProject;
    }

    public async Task SelectProjectAssetAsync(
        Guid projectId,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        var project = Projects.FirstOrDefault(item => item.Id == projectId);
        if (project is null)
        {
            await RefreshProjectsAsync(cancellationToken);
            project = Projects.FirstOrDefault(item => item.Id == projectId) ??
                throw new InvalidOperationException("项目不存在或已被删除。");
        }

        _suppressSelectionRefresh = true;
        try
        {
            SelectedProject = project;
            await RefreshSelectedProjectAssetsAsync(cancellationToken);
            SelectedProjectAsset = ProjectAssets.FirstOrDefault(item =>
                item.Asset.AssetId == assetId);
        }
        finally
        {
            _suppressSelectionRefresh = false;
        }
    }

    public async Task<ReaderFeed> SubscribeReaderAsync(
        ReaderSubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        var feed = await _services.Reader.SubscribeAsync(request, cancellationToken);
        await RefreshReaderAsync(cancellationToken);
        _suppressReaderSelectionActions = true;
        try
        {
            SelectedReaderNavigation = FindReaderNavigation(
                ReaderNavigationItems,
                ReaderFeedNavigationKey(feed.Id));
        }
        finally
        {
            _suppressReaderSelectionActions = false;
        }

        await RefreshReaderEntriesAsync(cancellationToken);
        return feed;
    }

    public async Task CreateDatabaseSnapshotsAsync(
        bool force = true,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _services.Workspace.GetAsync(cancellationToken)
            ?? throw new InvalidOperationException("尚未配置 CDSI 工作目录。");
        await _services.AssetDatabaseBackup.CreateSnapshotAsync(
            workspace.Path,
            force,
            cancellationToken);
        await _services.ReaderDatabaseBackup.CreateSnapshotAsync(
            workspace.Path,
            force,
            cancellationToken);
    }

    public Task RunUiOperationAsync(
        string initialStatus,
        bool allowCancel,
        Func<CancellationToken, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialStatus);
        ArgumentNullException.ThrowIfNull(operation);
        return RunOperationAsync(initialStatus, allowCancel, operation);
    }

    public Task RunIdleScanAsync(
        IReadOnlyCollection<Guid> scanRootIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanRootIds);
        cancellationToken.ThrowIfCancellationRequested();
        return RunScanPipelineAsync(
            FingerprintMode.DuplicateCandidates,
            scanRootIds,
            "正在执行空闲扫描");
    }

    public Task ReconcileLocalVolumesAsync()
    {
        return RunOperationAsync(
            "正在更新移动设备状态",
            allowCancel: false,
            async cancellationToken =>
            {
                var result = await _services.LocalVolumes.ReconcileAsync(
                    cancellationToken);
                if (result.HasChanges)
                {
                    await RefreshAssetsAsync(cancellationToken);
                    await RefreshAssetDirectoriesAsync(cancellationToken);
                }

                StatusText = FormatVolumeReconciliationStatus(result);
            });
    }

    public async Task PrepareForShutdownAsync(
        bool createSnapshots = true,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        _operationCancellation?.Cancel();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (!createSnapshots)
            {
                return;
            }

            var workspace = await _services.Workspace.GetAsync(cancellationToken);
            if (workspace is null)
            {
                return;
            }

            await _services.AssetDatabaseBackup.CreateSnapshotAsync(
                workspace.Path,
                force: false,
                cancellationToken);
            await _services.ReaderDatabaseBackup.CreateSnapshotAsync(
                workspace.Path,
                force: false,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LastError = $"退出前创建数据库快照失败：{exception.Message}";
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void CancelCurrentTask()
    {
        if (!_canCancelCurrentTask)
        {
            return;
        }

        StatusText = "正在取消当前任务";
        _operationCancellation?.Cancel();
    }

    internal void UpdateTransferProgress(ManagedAssetTransferProgress progress)
    {
        IsProgressIndeterminate = false;
        ProgressValue = progress.TotalBytes == 0
            ? 0
            : progress.ProcessedBytes * 100d / progress.TotalBytes;
        ProgressText =
            $"文件 {progress.ProcessedItems:N0}/{progress.TotalItems:N0} · " +
            $"{DisplayFormatting.FormatFileSize(progress.ProcessedBytes)}/" +
            DisplayFormatting.FormatFileSize(progress.TotalBytes);
        CurrentPath = progress.Message is null
            ? progress.CurrentPath ?? string.Empty
            : $"{progress.Message} · {progress.CurrentPath}";
    }

    internal void UpdateBackupProgress(
        ObjectStorageBackupProgress progress,
        string? profileName = null)
    {
        IsProgressIndeterminate = false;
        ProgressValue = progress.TotalBytes == 0
            ? 0
            : progress.UploadedBytes * 100d / progress.TotalBytes;
        ProgressText =
            $"云备份 {progress.ProcessedItems:N0}/{progress.TotalItems:N0} · " +
            $"{DisplayFormatting.FormatFileSize(progress.UploadedBytes)}/" +
            DisplayFormatting.FormatFileSize(progress.TotalBytes);
        var detail = progress.Message is null
            ? progress.CurrentPath ?? string.Empty
            : $"{progress.Message} · {progress.CurrentPath}";
        CurrentPath = string.IsNullOrWhiteSpace(profileName)
            ? detail
            : $"{profileName} · {detail}";
    }

    internal void UpdateRestoreProgress(ObjectStorageRestoreProgress progress)
    {
        IsProgressIndeterminate = false;
        ProgressValue = progress.TotalBytes == 0
            ? 0
            : progress.RestoredBytes * 100d / progress.TotalBytes;
        ProgressText =
            $"云取回 {progress.ProcessedItems:N0}/{progress.TotalItems:N0} · " +
            $"{DisplayFormatting.FormatFileSize(progress.RestoredBytes)}/" +
            DisplayFormatting.FormatFileSize(progress.TotalBytes);
        CurrentPath = progress.Message is null
            ? progress.CurrentPath ?? string.Empty
            : $"{progress.Message} · {progress.CurrentPath}";
    }

    internal void UpdateGitSyncProgress(GitProjectSyncProgress progress)
    {
        var copying = string.Equals(
            progress.Stage,
            "正在复制项目文件",
            StringComparison.Ordinal);
        IsProgressIndeterminate = !copying;
        if (copying)
        {
            ProgressValue = progress.TotalBytes > 0
                ? progress.ProcessedBytes * 100d / progress.TotalBytes
                : progress.TotalFiles > 0
                    ? progress.ProcessedFiles * 100d / progress.TotalFiles
                    : 0;
        }

        ProgressText =
            $"{progress.Stage} · 文件 {progress.ProcessedFiles:N0}/{progress.TotalFiles:N0} · " +
            $"{DisplayFormatting.FormatFileSize(progress.ProcessedBytes)}/" +
            DisplayFormatting.FormatFileSize(progress.TotalBytes);
        CurrentPath = progress.CurrentPath ?? string.Empty;
    }

    internal void SetOperationStatus(string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        StatusText = status.Trim();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationGate.Dispose();
        _services.Dispose();
    }

    private Task RunScanPipelineAsync(FingerprintMode fingerprintMode)
    {
        return RunScanPipelineAsync(
            fingerprintMode,
            scanRootIds: null,
            fingerprintMode == FingerprintMode.Complete
                ? "正在进行完整校验扫描"
                : "正在进行常规扫描");
    }

    private async Task RunScanPipelineAsync(
        FingerprintMode fingerprintMode,
        IReadOnlyCollection<Guid>? scanRootIds,
        string operationName)
    {
        await RunOperationAsync(
            operationName,
            allowCancel: true,
            async cancellationToken =>
            {
                var roots = await _services.Scan.ListScanRootsAsync(cancellationToken);
                if (!roots.Any(root => root.Enabled))
                {
                    StatusText = "没有已启用的扫描目录";
                    return;
                }

                IsProgressIndeterminate = true;
                ProgressText = "正在扫描已配置目录";
                var scanProgress = new Progress<ScanProgress>(UpdateScanProgress);
                var scan = scanRootIds is null
                    ? await _services.Scan.ScanConfiguredRootsAsync(
                        scanProgress,
                        cancellationToken)
                    : await _services.Scan.ScanRootsAsync(
                        scanRootIds,
                        scanProgress,
                        cancellationToken);
                if (scan.Cancelled)
                {
                    StatusText = "扫描已取消";
                    return;
                }

                IsProgressIndeterminate = false;
                ProgressValue = 0;
                StatusText = "正在提取元数据";
                var metadata = await _services.Metadata.ProcessPendingAsync(
                    new Progress<MetadataProgress>(UpdateMetadataProgress),
                    cancellationToken);
                if (metadata.Cancelled)
                {
                    StatusText = $"元数据提取已取消，已完成 {metadata.ExtractedFiles:N0} 个文件";
                    return;
                }

                ProgressValue = 0;
                StatusText = fingerprintMode == FingerprintMode.Complete
                    ? "正在进行完整哈希校验"
                    : "正在检查重复候选";
                var fingerprint = await _services.Fingerprints.ProcessPendingAsync(
                    fingerprintMode,
                    new Progress<FingerprintProgress>(UpdateFingerprintProgress),
                    cancellationToken);
                await RefreshAllPagesAsync(cancellationToken);
                StatusText = fingerprint.Cancelled
                    ? $"哈希已取消，已完成 {fingerprint.FingerprintedFiles:N0} 个文件"
                    : $"扫描完成 · 已索引 {scan.FilesIndexed:N0} · " +
                      $"元数据 {metadata.ExtractedFiles:N0} · 哈希 {fingerprint.FingerprintedFiles:N0}";
            });
    }

    private static string FormatVolumeReconciliationStatus(
        LocalVolumeReconciliationResult result)
    {
        if (!result.HasChanges)
        {
            return "本地卷状态已刷新";
        }

        if (result.OfflineVolumes > 0)
        {
            return $"移动设备已离线 {result.OfflineVolumes:N0} 个，资产记录已保留";
        }

        var remapped = result.RemappedScanRoots + result.RemappedAssetLocations;
        if (remapped > 0)
        {
            return $"移动设备位置已更新，重映射 {remapped:N0} 个本地位置";
        }

        return result.ReconnectedVolumes > 0
            ? $"移动设备已重新连接 {result.ReconnectedVolumes:N0} 个，文件位置等待确认"
            : "本地卷身份已更新";
    }

    private async Task RefreshWithStatusAsync()
    {
        await RunOperationAsync(
            "正在刷新",
            allowCancel: false,
            async cancellationToken =>
            {
                await RefreshAllPagesAsync(cancellationToken);
                StatusText = "全部视图已刷新";
            });
    }

    private async Task RefreshCloudBackupsWithStatusAsync()
    {
        await RunOperationAsync(
            "正在刷新云备份",
            allowCancel: false,
            async cancellationToken =>
            {
                await RefreshCloudBackupsAsync(cancellationToken);
                StatusText = "云备份列表已刷新";
            });
    }

    private async Task RefreshReaderFeedsFromNetworkAsync()
    {
        await RunOperationAsync(
            "正在刷新订阅",
            allowCancel: true,
            async cancellationToken =>
            {
                IsProgressIndeterminate = false;
                var summary = await _services.Reader.RefreshAllAsync(
                    new Progress<ReaderRefreshProgress>(UpdateReaderProgress),
                    cancellationToken);
                await RefreshReaderAsync(cancellationToken);
                StatusText = summary.Failed == 0
                    ? $"订阅刷新完成 · 新增 {summary.NewEntries:N0} 条"
                    : $"订阅刷新完成 · 成功 {summary.Succeeded:N0} · 失败 {summary.Failed:N0}";
            });
    }

    private async Task SearchReaderEntriesWithStatusAsync()
    {
        await RunOperationAsync(
            "正在搜索订阅条目",
            allowCancel: false,
            async cancellationToken =>
            {
                await RefreshReaderEntriesAsync(cancellationToken);
                StatusText = $"RSS 搜索完成 · {ReaderEntries.Count:N0} 条";
            });
    }

    private async Task ApplyAssetFilterAsync()
    {
        try
        {
            _appliedAssetFilter = BuildAssetFilter();
        }
        catch (ArgumentException exception)
        {
            LastError = exception.Message;
            StatusText = exception.Message;
            return;
        }

        _assetPageIndex = 0;
        await RunOperationAsync(
            "正在筛选资产",
            allowCancel: false,
            async cancellationToken =>
            {
                await RefreshAssetsAsync(cancellationToken);
                StatusText = AssetResultText;
            });
    }

    private async Task ResetAssetFiltersAsync()
    {
        AssetSearchText = string.Empty;
        SelectedFileType = FileTypeOptions[0];
        SelectedExtension = ExtensionOptions[0];
        SelectedTag = TagOptions[0];
        CreatedFrom = null;
        CreatedTo = null;
        _appliedAssetFilter = AssetListFilter.Empty;
        _assetPageIndex = 0;
        await RunOperationAsync(
            "正在重置资产筛选",
            allowCancel: false,
            async cancellationToken =>
            {
                await RefreshAssetsAsync(cancellationToken);
                StatusText = "已显示全部资产";
            });
    }

    private async Task NavigateAssetPageAsync(int delta)
    {
        var target = Math.Clamp(
            _assetPageIndex + delta,
            0,
            CalculatePageCount(_assetTotalItems, SelectedPageSize) - 1);
        if (target == _assetPageIndex)
        {
            return;
        }

        _assetPageIndex = target;
        await RunOperationAsync(
            "正在加载资产页",
            allowCancel: false,
            async cancellationToken =>
            {
                await RefreshAssetsAsync(cancellationToken);
                StatusText = $"当前页 {Assets.Count:N0} 个资产";
            });
    }

    private async Task RefreshSelectedProjectAssetsAsync(
        CancellationToken cancellationToken = default)
    {
        var project = SelectedProject;
        var refreshVersion = Interlocked.Increment(ref _projectAssetRefreshVersion);
        await RefreshSelectedProjectAssetsAsync(project, refreshVersion, cancellationToken);
    }

    private async Task RefreshSelectedProjectAssetsAsync(
        ProjectRowViewModel? project,
        long refreshVersion,
        CancellationToken cancellationToken)
    {
        if (project is null)
        {
            if (IsCurrentProjectAssetRefresh(refreshVersion, project))
            {
                ProjectAssets.Clear();
                SelectedProjectAsset = null;
                RaiseCommandStates();
            }

            return;
        }

        var selectedId = SelectedProjectAsset?.Asset.AssetId;
        var members = await _services.AssetCollections.GetMembersAsync(
            project.Id,
            cancellationToken);
        if (!IsCurrentProjectAssetRefresh(refreshVersion, project))
        {
            return;
        }

        Replace(
            ProjectAssets,
            members.Select(item => new ProjectAssetRowViewModel { Source = item }));
        SelectedProjectAsset = ProjectAssets.FirstOrDefault(
            item => item.Asset.AssetId == selectedId) ?? ProjectAssets.FirstOrDefault();
        RaiseCommandStates();
    }

    private void PopulateReaderNavigation(
        IReadOnlyList<ReaderFeedSummary> feeds,
        string selectedNavigationKey)
    {
        var feedRows = feeds
            .Select(item => new ReaderFeedRowViewModel { Source = item })
            .ToArray();
        Replace(ReaderFeeds, feedRows);

        var sourceChildren = new List<ReaderNavigationRowViewModel>();
        foreach (var folderGroup in feedRows.GroupBy(
                     item => item.Source.Feed.FolderName ?? string.Empty,
                     StringComparer.OrdinalIgnoreCase))
        {
            var feedNodes = folderGroup
                .Select(CreateReaderFeedNavigation)
                .ToArray();
            if (string.IsNullOrWhiteSpace(folderGroup.Key))
            {
                sourceChildren.AddRange(feedNodes);
                continue;
            }

            sourceChildren.Add(new ReaderNavigationRowViewModel
            {
                NavigationKey = $"folder:{folderGroup.Key.ToUpperInvariant()}",
                Title = folderGroup.Key,
                Children = feedNodes
            });
        }

        var totalEntries = feeds.Sum(item => (long)item.EntryCount);
        var unreadEntries = feeds.Sum(item => (long)item.UnreadCount);
        ReaderNavigationRowViewModel[] navigation =
        [
            new()
            {
                NavigationKey = ReaderAllNavigationKey,
                Title = "全部",
                CountText = totalEntries.ToString("N0")
            },
            new()
            {
                NavigationKey = "unread",
                Title = "未读",
                CountText = unreadEntries.ToString("N0"),
                UnreadOnly = true
            },
            new()
            {
                NavigationKey = "starred",
                Title = "收藏",
                StarredOnly = true
            },
            new()
            {
                NavigationKey = "sources",
                Title = "订阅源",
                Children = sourceChildren
            }
        ];

        _suppressReaderSelectionActions = true;
        try
        {
            Replace(ReaderNavigationItems, navigation);
            SelectedReaderNavigation = FindReaderNavigation(
                    ReaderNavigationItems,
                    selectedNavigationKey) ??
                ReaderNavigationItems.FirstOrDefault();
        }
        finally
        {
            _suppressReaderSelectionActions = false;
        }
    }

    private static ReaderNavigationRowViewModel CreateReaderFeedNavigation(
        ReaderFeedRowViewModel feed)
    {
        return new ReaderNavigationRowViewModel
        {
            NavigationKey = ReaderFeedNavigationKey(feed.Id),
            Title = feed.Title,
            CountText = feed.UnreadCountText,
            Feed = feed
        };
    }

    private static string ReaderFeedNavigationKey(Guid feedId) => $"feed:{feedId:D}";

    private static ReaderNavigationRowViewModel? FindReaderNavigation(
        IEnumerable<ReaderNavigationRowViewModel> navigation,
        string key)
    {
        foreach (var item in navigation)
        {
            if (string.Equals(item.NavigationKey, key, StringComparison.Ordinal))
            {
                return item;
            }

            var child = FindReaderNavigation(item.Children, key);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private async Task RefreshReaderEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        var navigation = SelectedReaderNavigation;
        var selectedEntryId = SelectedReaderEntry?.Id;
        var entries = await _services.Reader.ListEntriesAsync(
            new ReaderEntryQuery(
                FeedId: navigation?.FeedId,
                UnreadOnly: navigation?.UnreadOnly ?? false,
                StarredOnly: navigation?.StarredOnly ?? false,
                SearchText: ReaderSearchText,
                Limit: 500),
            cancellationToken);
        _suppressReaderSelectionActions = true;
        try
        {
            _pendingReaderAutoReadEntryId = null;
            Replace(
                ReaderEntries,
                entries.Select(item => new ReaderEntryRowViewModel { Source = item }));
            SelectedReaderEntry = ReaderEntries.FirstOrDefault(
                item => item.Id == selectedEntryId) ?? ReaderEntries.FirstOrDefault();
        }
        finally
        {
            _suppressReaderSelectionActions = false;
        }

        ReaderSummaryText =
            $"{ReaderFeeds.Count:N0} 个订阅 · 当前 {ReaderEntries.Count:N0} 条";
        if (_readerAutoReadOnActivationPending)
        {
            QueueSelectedReaderEntryAutoRead();
        }
    }

    private void QueueSelectedReaderEntryAutoRead()
    {
        if (_selectedTabIndex != ReaderTabIndex || !_services.ReaderAvailable)
        {
            _readerAutoReadOnActivationPending = false;
            _pendingReaderAutoReadEntryId = null;
            return;
        }

        var selected = SelectedReaderEntry;
        if (selected is null)
        {
            return;
        }

        _readerAutoReadOnActivationPending = false;
        if (selected.Source.Entry.IsRead)
        {
            _pendingReaderAutoReadEntryId = null;
            return;
        }

        _pendingReaderAutoReadEntryId = selected.Id;
        _ = MarkPendingReaderEntryReadAsync();
    }

    private async Task MarkPendingReaderEntryReadAsync()
    {
        if (_disposed || !_initialized || _pendingReaderAutoReadEntryId is not { } entryId)
        {
            return;
        }

        var entered = false;
        try
        {
            entered = await _operationGate.WaitAsync(0);
            if (!entered ||
                _selectedTabIndex != ReaderTabIndex ||
                _pendingReaderAutoReadEntryId != entryId ||
                SelectedReaderEntry?.Id != entryId ||
                SelectedReaderEntry.Source.Entry.IsRead)
            {
                return;
            }

            _pendingReaderAutoReadEntryId = null;
            await _services.Reader.SetEntryReadAsync(entryId, true);

            var row = ReaderEntries.FirstOrDefault(item => item.Id == entryId);
            if (row is not null)
            {
                var updated = new ReaderEntryRowViewModel
                {
                    Source = row.Source with
                    {
                        Entry = row.Source.Entry with
                        {
                            IsRead = true,
                            ReadAt = DateTimeOffset.UtcNow
                        }
                    }
                };
                var index = ReaderEntries.IndexOf(row);
                _suppressReaderSelectionActions = true;
                try
                {
                    ReaderEntries[index] = updated;
                    if (SelectedReaderEntry?.Id == entryId)
                    {
                        SelectedReaderEntry = updated;
                    }
                }
                finally
                {
                    _suppressReaderSelectionActions = false;
                }
            }

            var selectedNavigationKey = SelectedReaderNavigation?.NavigationKey ??
                ReaderAllNavigationKey;
            var feeds = await _services.Reader.ListFeedsAsync();
            PopulateReaderNavigation(feeds, selectedNavigationKey);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Application shutdown won the race with this best-effort UI update.
        }
        catch (Exception exception)
        {
            if (_pendingReaderAutoReadEntryId == entryId)
            {
                _pendingReaderAutoReadEntryId = null;
            }

            LastError = exception.Message;
            StatusText = $"无法更新阅读状态：{exception.Message}";
            _services.RuntimeLog.WriteError("自动标记 RSS 条目为已读失败", exception);
        }
        finally
        {
            if (entered && !_disposed)
            {
                _operationGate.Release();
                if (_pendingReaderAutoReadEntryId is not null && !_disposed)
                {
                    QueueSelectedReaderEntryAutoRead();
                }
            }
        }
    }

    private void ApplyCloudBackupSearch()
    {
        var selectedName = SelectedCloudBackupProject?.Name;
        var term = CloudBackupSearchText.Trim();
        var filtered = string.IsNullOrEmpty(term)
            ? _allCloudBackups.ToArray()
            : _allCloudBackups.Where(backup => CloudBackupMatches(backup, term)).ToArray();
        var groups = GroupCloudBackups(filtered);
        Replace(CloudBackupProjects, groups);
        SelectedCloudBackupProject = CloudBackupProjects.FirstOrDefault(project =>
            string.Equals(project.Name, selectedName, StringComparison.OrdinalIgnoreCase)) ??
            CloudBackupProjects.FirstOrDefault();
        if (SelectedCloudBackupProject is null)
        {
            CloudBackupSummaryText = string.IsNullOrEmpty(term)
                ? "没有云端副本"
                : "没有符合条件的云端副本";
        }

        StatusText = string.IsNullOrEmpty(term)
            ? "已显示全部云备份"
            : $"云备份搜索完成 · {filtered.Length:N0} 个结果";
    }

    private void PopulateSelectedCloudBackupProject()
    {
        var project = SelectedCloudBackupProject;
        if (project is null)
        {
            CloudBackups.Clear();
            SelectedCloudBackup = null;
            CloudBackupSummaryText = "没有符合条件的云端副本";
            return;
        }

        Replace(
            CloudBackups,
            project.Backups.Select(backup => new CloudBackupRowViewModel
            {
                Source = backup,
                ProjectName = project.Name
            }));
        SelectedCloudBackup = CloudBackups.FirstOrDefault();
        CloudBackupSummaryText =
            $"{project.Name} · {project.AssetCountText} 个资源 · " +
            $"{project.BackupCountText} 个副本 · {project.TotalSizeText}";
    }

    private void ApplyGitProjectSearch()
    {
        (Guid ProjectId, Guid ProfileId)? selectedKey = SelectedGitProject is null
            ? null
            : (SelectedGitProject.Record.ProjectId, SelectedGitProject.Record.ProfileId);
        var term = GitProjectSearchText.Trim();
        var filtered = string.IsNullOrEmpty(term)
            ? _allGitProjects
            : _allGitProjects.Where(item => GitProjectMatches(item, term)).ToList();
        Replace(GitProjects, filtered);
        SelectedGitProject = selectedKey is null
            ? GitProjects.FirstOrDefault()
            : GitProjects.FirstOrDefault(item =>
                item.Record.ProjectId == selectedKey.Value.ProjectId &&
                item.Record.ProfileId == selectedKey.Value.ProfileId) ??
              GitProjects.FirstOrDefault();
        GitProjectSummaryText =
            $"同步记录 {_allGitProjects.Count:N0} · 当前显示 {GitProjects.Count:N0}";
    }

    private void ResetGitProjectSearch()
    {
        GitProjectSearchText = string.Empty;
        ApplyGitProjectSearch();
        StatusText = "已显示全部 Git 项目";
    }

    private AssetListFilter BuildAssetFilter()
    {
        if (CreatedFrom is not null && CreatedTo is not null &&
            CreatedFrom.Value.LocalDateTime.Date > CreatedTo.Value.LocalDateTime.Date)
        {
            throw new ArgumentException("创建时间的开始日期不能晚于结束日期。");
        }

        return new AssetListFilter(
            SelectedFileType.Value,
            CreatedFrom is null ? null : StartOfLocalDay(CreatedFrom.Value),
            CreatedTo is null ? null : StartOfLocalDay(CreatedTo.Value.AddDays(1)),
            SelectedExtension.Value,
            SelectedTag.TagId,
            AssetSearchText);
    }

    private void UpdateAssetFilterOptions(
        IReadOnlyList<string> extensions,
        IReadOnlyList<AssetTagSummary> tags)
    {
        var selectedExtension = SelectedExtension.Value;
        var extensionOptions = extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(extension => new AssetExtensionOption(extension, extension))
            .Prepend(new AssetExtensionOption("全部扩展名", null));
        Replace(ExtensionOptions, extensionOptions);
        SelectedExtension = ExtensionOptions.FirstOrDefault(option => string.Equals(
            option.Value,
            selectedExtension,
            StringComparison.OrdinalIgnoreCase)) ?? ExtensionOptions[0];

        var selectedTag = SelectedTag.TagId;
        var tagOptions = tags
            .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(tag => new AssetTagOption(
                $"{tag.Name} ({tag.AssetCount:N0})",
                tag.Id))
            .Prepend(new AssetTagOption("全部标签", null));
        Replace(TagOptions, tagOptions);
        SelectedTag = TagOptions.FirstOrDefault(option => option.TagId == selectedTag) ??
            TagOptions[0];
    }

    private void UpdateAssetPaginationText()
    {
        var pageCount = CalculatePageCount(_assetTotalItems, SelectedPageSize);
        var offset = _assetPageIndex * SelectedPageSize;
        var first = _assetTotalItems == 0 ? 0 : offset + 1;
        var last = Math.Min(_assetTotalItems, offset + SelectedPageSize);
        AssetPageText = _assetTotalItems == 0
            ? "第 1 / 1 页 · 0 条"
            : $"第 {_assetPageIndex + 1:N0} / {pageCount:N0} 页 · " +
              $"{first:N0}-{last:N0} / {_assetTotalItems:N0}";
        AssetResultText = _appliedAssetFilter.IsEmpty
            ? $"全部 {_unfilteredAssetTotalItems:N0}"
            : $"筛选结果 {_assetTotalItems:N0} / {_unfilteredAssetTotalItems:N0}";
        OnPropertyChanged(nameof(CanPreviousAssetPage));
        OnPropertyChanged(nameof(CanNextAssetPage));
        RaiseCommandStates();
    }

    private void UpdateScanProgress(ScanProgress progress)
    {
        IsProgressIndeterminate = true;
        ProgressText =
            $"发现 {progress.FilesDiscovered:N0} · 已索引 {progress.FilesIndexed:N0} · " +
            $"错误 {progress.Errors:N0}";
        CurrentPath = progress.CurrentPath ?? progress.Message ?? string.Empty;
    }

    private void UpdateMetadataProgress(MetadataProgress progress)
    {
        IsProgressIndeterminate = false;
        ProgressValue = progress.TotalFiles == 0
            ? 0
            : progress.CompletedFiles * 100d / progress.TotalFiles;
        ProgressText =
            $"元数据 {progress.CompletedFiles:N0}/{progress.TotalFiles:N0} · " +
            $"已提取 {progress.ExtractedFiles:N0} · 错误 {progress.Errors:N0}";
        CurrentPath = progress.Message ?? progress.CurrentPath ?? string.Empty;
    }

    private void UpdateFingerprintProgress(FingerprintProgress progress)
    {
        IsProgressIndeterminate = false;
        ProgressValue = progress.TotalBytes == 0
            ? 0
            : progress.ProcessedBytes * 100d / progress.TotalBytes;
        ProgressText =
            $"哈希 {progress.CompletedFiles:N0}/{progress.TotalFiles:N0} · " +
            $"已完成 {progress.FingerprintedFiles:N0} · 错误 {progress.Errors:N0}";
        CurrentPath = progress.Message ?? progress.CurrentPath ?? string.Empty;
    }

    private void UpdateReaderProgress(ReaderRefreshProgress progress)
    {
        IsProgressIndeterminate = false;
        ProgressValue = progress.Total == 0
            ? 0
            : progress.Completed * 100d / progress.Total;
        ProgressText = $"RSS订阅 · {progress.Completed:N0}/{progress.Total:N0}";
        CurrentPath = progress.Error is null
            ? progress.FeedTitle
            : $"{progress.FeedTitle} · {progress.Error}";
    }

    private async Task RunOperationAsync(
        string initialStatus,
        bool allowCancel,
        Func<CancellationToken, Task> operation,
        bool rethrowErrors = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operationGate.WaitAsync(0))
        {
            return;
        }

        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _canCancelCurrentTask = allowCancel;
        IsBusy = true;
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = initialStatus;
        CurrentPath = string.Empty;
        StatusText = initialStatus;
        LastError = null;
        try
        {
            await operation(_operationCancellation.Token);
            StatusText = ResolveSuccessfulOperationStatus(initialStatus, StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = $"{initialStatus}已取消";
            if (rethrowErrors)
            {
                throw;
            }
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusText = $"{initialStatus}失败：{exception.Message}";
            _services.RuntimeLog.WriteError($"{initialStatus}失败", exception);
            if (rethrowErrors)
            {
                throw;
            }
        }
        finally
        {
            _canCancelCurrentTask = false;
            IsProgressIndeterminate = false;
            IsBusy = false;
            ProgressText = "就绪";
            CurrentPath = string.Empty;
            _operationGate.Release();
            if (_pendingReaderAutoReadEntryId is not null && !_disposed)
            {
                _ = MarkPendingReaderEntryReadAsync();
            }
        }
    }

    internal static string ResolveSuccessfulOperationStatus(
        string initialStatus,
        string currentStatus) =>
        string.Equals(currentStatus, initialStatus, StringComparison.Ordinal)
            ? "就绪"
            : currentStatus;

    private AsyncRelayCommand CreateAsyncCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null)
    {
        return new AsyncRelayCommand(execute, canExecute, exception =>
        {
            LastError = exception.Message;
            StatusText = exception.Message;
        });
    }

    private RelayCommand CreateActionCommand(
        MainViewAction action,
        Func<bool>? canExecute = null,
        Func<object?>? context = null)
    {
        return new RelayCommand(
            () => UiActionRequested?.Invoke(
                this,
                new MainViewActionRequestedEventArgs(action, context?.Invoke())),
            canExecute);
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
        yield return StartStandardScanCommand;
        yield return StartFullScanCommand;
        yield return CancelCurrentTaskCommand;
        yield return RefreshCommand;
        yield return CreateProjectCommand;
        yield return OpenWorkspaceCommand;
        yield return OpenDataDirectoryCommand;
        yield return OpenSettingsCommand;
        yield return ShowDataProtectionCommand;
        yield return ShowTaskCenterCommand;
        yield return ShowRuntimeLogCommand;
        yield return CheckForUpdatesCommand;
        yield return OpenDocumentationCommand;
        yield return ShowPrivacyCommand;
        yield return ShowLicenseCommand;
        yield return ShowThirdPartyNoticesCommand;
        yield return ShowAboutCommand;
        yield return OpenAssetLocationCommand;
        yield return OpenAssetProjectCommand;
        yield return ShowAssetDetailsCommand;
        yield return ManageAssetTagsCommand;
        yield return AddAssetsToProjectCommand;
        yield return PublishAssetCommand;
        yield return CopyAssetsToWorkspaceCommand;
        yield return MoveAssetsToWorkspaceCommand;
        yield return BackupAssetsCommand;
        yield return RestoreAssetsCommand;
        yield return RemoveAssetsCommand;
        yield return SearchAssetsCommand;
        yield return ResetAssetFiltersCommand;
        yield return PreviousAssetPageCommand;
        yield return NextAssetPageCommand;
        yield return OpenAssetDirectoryCommand;
        yield return RemoveAssetDirectoryCommand;
        yield return OpenDuplicateLocationCommand;
        yield return ShowDuplicateDetailsCommand;
        yield return SyncProjectCommand;
        yield return EditProjectCommand;
        yield return DeleteProjectCommand;
        yield return SyncProjectToGitCommand;
        yield return OpenProjectAssetLocationCommand;
        yield return RemoveProjectAssetsCommand;
        yield return SearchCloudBackupsCommand;
        yield return RefreshCloudBackupsCommand;
        yield return DeleteCloudBackupProjectCommand;
        yield return OpenCloudBackupLocationCommand;
        yield return RestoreCloudBackupCommand;
        yield return DeleteCloudBackupCommand;
        yield return SearchGitProjectsCommand;
        yield return ResetGitProjectSearchCommand;
        yield return SyncGitProjectCommand;
        yield return OpenGitProjectCommand;
        yield return OpenGitRepositoryCommand;
        yield return CopyGitRepositoryUrlCommand;
        yield return AddReaderFeedCommand;
        yield return RefreshReaderFeedsCommand;
        yield return SearchReaderEntriesCommand;
        yield return RefreshSelectedReaderFeedCommand;
        yield return OpenReaderFeedSiteCommand;
        yield return RemoveReaderFeedCommand;
        yield return ImportReaderOpmlCommand;
        yield return ExportReaderOpmlCommand;
        yield return ImportReaderDataCommand;
        yield return ExportReaderDataCommand;
        yield return OpenReaderEntryCommand;
        yield return ToggleReaderEntryReadCommand;
        yield return ToggleReaderEntryStarCommand;
    }

    private async Task RefreshExtensionOptionsGuardedAsync()
    {
        try
        {
            var extensions = await _services.Scan.ListAssetExtensionsAsync(
                SelectedFileType.Value);
            UpdateAssetFilterOptions(
                extensions,
                await _services.AssetTags.ListAsync());
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
    }

    private async Task RefreshAssetPageGuardedAsync()
    {
        try
        {
            await RunOperationAsync(
                "正在加载资产页",
                allowCancel: false,
                RefreshAssetsAsync);
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }
    }

    internal static bool IsCurrentProjectAssetRefresh(
        long refreshVersion,
        long currentRefreshVersion,
        Guid? requestedProjectId,
        Guid? selectedProjectId) =>
        refreshVersion == currentRefreshVersion &&
        requestedProjectId == selectedProjectId;

    private bool IsCurrentProjectAssetRefresh(
        long refreshVersion,
        ProjectRowViewModel? requestedProject) =>
        IsCurrentProjectAssetRefresh(
            refreshVersion,
            Volatile.Read(ref _projectAssetRefreshVersion),
            requestedProject?.Id,
            SelectedProject?.Id);

    private async Task RefreshSelectedProjectAssetsGuardedAsync(
        ProjectRowViewModel? project,
        long refreshVersion)
    {
        try
        {
            await RefreshSelectedProjectAssetsAsync(project, refreshVersion, default);
        }
        catch (Exception exception)
        {
            if (IsCurrentProjectAssetRefresh(refreshVersion, project))
            {
                LastError = exception.Message;
                StatusText = $"无法读取项目资产：{exception.Message}";
            }
        }
    }

    private async Task RefreshReaderEntriesGuardedAsync()
    {
        try
        {
            await RefreshReaderEntriesAsync();
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusText = $"无法读取 RSS 条目：{exception.Message}";
        }
    }

    private static DateTimeOffset StartOfLocalDay(DateTimeOffset value)
    {
        var localDate = DateTime.SpecifyKind(value.LocalDateTime.Date, DateTimeKind.Unspecified);
        var utcDate = TimeZoneInfo.ConvertTimeToUtc(localDate, TimeZoneInfo.Local);
        return new DateTimeOffset(utcDate, TimeSpan.Zero);
    }

    private static long CalculatePageCount(long totalItems, int pageSize) =>
        totalItems == 0 ? 1 : ((totalItems - 1) / pageSize) + 1;

    private static bool CloudBackupMatches(
        ManagedObjectStorageBackup backup,
        string term)
    {
        var source = backup.Source;
        string?[] candidates =
        [
            source.OriginalFilename,
            DisplayFormatting.GetCloudFilename(source.Location.ObjectKey),
            source.Location.ObjectKey,
            backup.Profile?.DisplayName,
            DisplayFormatting.FormatStorageProvider(backup.Profile?.Provider),
            source.LocalPath,
            string.Join("、", source.ProjectNames)
        ];
        return candidates.Any(candidate =>
            candidate?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static IReadOnlyList<CloudBackupProjectRowViewModel> GroupCloudBackups(
        IEnumerable<ManagedObjectStorageBackup> backups)
    {
        return backups
            .GroupBy(
                backup => GetCloudProjectName(backup.Source.Location.ObjectKey) ??
                    "未归属项目",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new CloudBackupProjectRowViewModel
            {
                Name = group.Key,
                IsUnassigned = string.Equals(
                    group.Key,
                    "未归属项目",
                    StringComparison.Ordinal),
                Backups = group.ToArray()
            })
            .ToArray();
    }

    private static string? GetCloudProjectName(string objectKey)
    {
        var separator = objectKey.LastIndexOf('/');
        if (separator <= 0)
        {
            return null;
        }

        var directory = objectKey[..separator].Trim();
        return directory.Length == 0 || directory.Contains('/') ? null : directory;
    }

    private static bool GitProjectMatches(GitProjectRowViewModel item, string term)
    {
        string[] candidates =
        [
            item.ProjectName,
            item.ProfileName,
            item.ProviderText,
            item.RepositoryUrl,
            item.Branch,
            item.Record.CommitId,
            item.LocalStateText
        ];
        return candidates.Any(candidate =>
            candidate.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static void Replace<T>(
        ObservableCollection<T> collection,
        IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }
    }
}
