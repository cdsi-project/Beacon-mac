using System.Reflection;
using CDSI.Agent.Application.Assets;
using CDSI.Agent.Application.Collections;
using CDSI.Agent.Application.Fingerprints;
using CDSI.Agent.Application.Git;
using CDSI.Agent.Application.Metadata;
using CDSI.Agent.Application.OpenWeb;
using CDSI.Agent.Application.Reader;
using CDSI.Agent.Application.Scanning;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Application.Transfers;
using CDSI.Agent.Application.Workspaces;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Identity;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Infrastructure.FileSystem;
using CDSI.Agent.Infrastructure.Fingerprints;
using CDSI.Agent.Infrastructure.Git;
using CDSI.Agent.Infrastructure.Identity;
using CDSI.Agent.Infrastructure.Metadata;
using CDSI.Agent.Infrastructure.OpenWeb;
using CDSI.Agent.Infrastructure.Persistence;
using CDSI.Agent.Infrastructure.Reader;
using CDSI.Agent.Infrastructure.Storage;
using CDSI.Agent.Mac.Platform;

namespace CDSI.Agent.Mac.Services;

/// <summary>
/// Owns the application's long-lived services and the resources behind them.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly HttpClient _readerHttpClient;
    private readonly HttpClient _openWebHttpClient;
    private bool _disposed;

    private AppServices(
        string dataDirectory,
        string assetDatabasePath,
        string readerDatabasePath,
        string applicationVersion,
        ClientIdentity clientIdentity,
        SqliteAssetRepository assetRepository,
        SqliteReaderRepository readerRepository,
        HttpClient readerHttpClient,
        HttpClient openWebHttpClient,
        RuntimeLogService runtimeLog,
        ISecretStore secretStore,
        ScanApplicationService scanService,
        FingerprintApplicationService fingerprintService,
        MetadataExtractionApplicationService metadataService,
        ReaderApplicationService readerService,
        WorkspaceApplicationService workspaceService,
        ScanRootManagementService scanRootService,
        LocalVolumeReconciliationService volumeReconciliationService,
        ObjectStorageProfileService objectStorageProfileService,
        OpenWebSettingsService openWebSettingsService,
        GitProfileService gitProfileService,
        OpenWebArticlePublishingService openWebPublishingService,
        ObjectStorageBackupService objectStorageBackupService,
        ObjectStorageRestoreService objectStorageRestoreService,
        ObjectStorageManagementService objectStorageManagementService,
        AssetCollectionService assetCollectionService,
        GitProjectSyncService gitProjectSyncService,
        AssetTagService assetTagService,
        ManagedAssetTransferService transferService,
        LocalDatabaseBackupService assetDatabaseBackupService,
        LocalDatabaseBackupService readerDatabaseBackupService,
        LocalStateProtectionService stateProtectionService,
        PendingStateRestoreService pendingStateRestoreService)
    {
        DataDirectory = dataDirectory;
        AssetDatabasePath = assetDatabasePath;
        ReaderDatabasePath = readerDatabasePath;
        ApplicationVersion = applicationVersion;
        ClientIdentity = clientIdentity;
        AssetRepository = assetRepository;
        ReaderRepository = readerRepository;
        _readerHttpClient = readerHttpClient;
        _openWebHttpClient = openWebHttpClient;
        RuntimeLog = runtimeLog;
        SecretStore = secretStore;
        Scan = scanService;
        Fingerprints = fingerprintService;
        Metadata = metadataService;
        Reader = readerService;
        Workspace = workspaceService;
        ScanRoots = scanRootService;
        LocalVolumes = volumeReconciliationService;
        ObjectStorageProfiles = objectStorageProfileService;
        OpenWebSettings = openWebSettingsService;
        GitProfiles = gitProfileService;
        OpenWebPublishing = openWebPublishingService;
        ObjectStorageBackup = objectStorageBackupService;
        ObjectStorageRestore = objectStorageRestoreService;
        ObjectStorageManagement = objectStorageManagementService;
        AssetCollections = assetCollectionService;
        GitProjectSync = gitProjectSyncService;
        AssetTags = assetTagService;
        Transfers = transferService;
        AssetDatabaseBackup = assetDatabaseBackupService;
        ReaderDatabaseBackup = readerDatabaseBackupService;
        StateProtection = stateProtectionService;
        PendingStateRestore = pendingStateRestoreService;
    }

    public string DataDirectory { get; }

    public string AssetDatabasePath { get; }

    public string ReaderDatabasePath { get; }

    public string ApplicationVersion { get; }

    public ClientIdentity ClientIdentity { get; }

    public SqliteAssetRepository AssetRepository { get; }

    public SqliteReaderRepository ReaderRepository { get; }

    public ISecretStore SecretStore { get; }

    public RuntimeLogService RuntimeLog { get; }

    public ScanApplicationService Scan { get; }

    public FingerprintApplicationService Fingerprints { get; }

    public MetadataExtractionApplicationService Metadata { get; }

    public ReaderApplicationService Reader { get; }

    public bool ReaderAvailable { get; private set; } = true;

    public string? ReaderInitializationError { get; private set; }

    public WorkspaceApplicationService Workspace { get; }

    public ScanRootManagementService ScanRoots { get; }

    public LocalVolumeReconciliationService LocalVolumes { get; }

    public ObjectStorageProfileService ObjectStorageProfiles { get; }

    public OpenWebSettingsService OpenWebSettings { get; }

    public GitProfileService GitProfiles { get; }

    public OpenWebArticlePublishingService OpenWebPublishing { get; }

    public ObjectStorageBackupService ObjectStorageBackup { get; }

    public ObjectStorageRestoreService ObjectStorageRestore { get; }

    public ObjectStorageManagementService ObjectStorageManagement { get; }

    public AssetCollectionService AssetCollections { get; }

    public GitProjectSyncService GitProjectSync { get; }

    public AssetTagService AssetTags { get; }

    public ManagedAssetTransferService Transfers { get; }

    public LocalDatabaseBackupService AssetDatabaseBackup { get; }

    public LocalDatabaseBackupService ReaderDatabaseBackup { get; }

    public LocalStateProtectionService StateProtection { get; }

    public PendingStateRestoreService PendingStateRestore { get; }

    public static AppServices CreateDefault(
        string? dataDirectory = null,
        string? applicationVersion = null,
        RuntimeLogService? runtimeLog = null)
    {
        var normalizedDataDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(dataDirectory)
                ? GetDefaultDataDirectory()
                : dataDirectory);
        Directory.CreateDirectory(normalizedDataDirectory);

        var version = string.IsNullOrWhiteSpace(applicationVersion)
            ? GetApplicationVersion()
            : applicationVersion.Trim();
        var assetDatabasePath = Path.Combine(normalizedDataDirectory, "cdsi.db");
        var readerDatabasePath = Path.Combine(normalizedDataDirectory, "reader.db");
        var clientIdentity = new FileClientIdentityProvider(normalizedDataDirectory)
            .GetOrCreate();

        var assetRepository = new SqliteAssetRepository(assetDatabasePath);
        var readerRepository = new SqliteReaderRepository(readerDatabasePath);
        var readerHttpClient = ReaderHttpFeedClient.CreateHttpClient(version);
        var openWebHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        var secretStore = new MacKeychainSecretStore();
        runtimeLog ??= new RuntimeLogService(normalizedDataDirectory);
        var fingerprintEngine = new Sha256FileFingerprintService();
        var workspaceProvisioner = new WorkspaceProvisioner();
        IObjectStorageAdapter[] storageAdapters =
        [
            new AliyunOssStorageAdapter(),
            new S3CompatibleStorageAdapter(ObjectStorageProvider.QiniuKodo),
            new S3CompatibleStorageAdapter(ObjectStorageProvider.TencentCos)
        ];

        var scanService = new ScanApplicationService(
            new FileSystemScanner(),
            assetRepository);
        var fingerprintService = new FingerprintApplicationService(
            fingerprintEngine,
            assetRepository);
        var metadataService = new MetadataExtractionApplicationService(
            [new TagLibMetadataExtractor(), new GenericMetadataExtractor()],
            assetRepository);
        var readerService = new ReaderApplicationService(
            readerRepository,
            new ReaderHttpFeedClient(
                readerHttpClient,
                new SyndicationFeedParser()),
            new OpmlSubscriptionExchange());
        var workspaceService = new WorkspaceApplicationService(
            assetRepository,
            workspaceProvisioner);
        var scanRootService = new ScanRootManagementService(assetRepository);
        var volumeReconciliationService = new LocalVolumeReconciliationService(
            new MacLocalVolumeProvider(),
            assetRepository);
        var objectStorageProfileService = new ObjectStorageProfileService(
            assetRepository,
            secretStore);
        var openWebSettingsService = new OpenWebSettingsService(
            assetRepository,
            secretStore);
        var gitProfileService = new GitProfileService(
            assetRepository,
            secretStore);
        var openWebPublishingService = new OpenWebArticlePublishingService(
            openWebSettingsService,
            assetRepository,
            new LocalOpenWebArticleContentReader(),
            new WordPressArticlePublisher(openWebHttpClient));
        var objectStorageBackupService = new ObjectStorageBackupService(
            assetRepository,
            assetRepository,
            objectStorageProfileService,
            fingerprintEngine,
            storageAdapters);
        var objectStorageRestoreService = new ObjectStorageRestoreService(
            assetRepository,
            assetRepository,
            assetRepository,
            objectStorageProfileService,
            workspaceProvisioner,
            storageAdapters);
        var objectStorageManagementService = new ObjectStorageManagementService(
            assetRepository,
            objectStorageProfileService,
            storageAdapters);
        var assetCollectionService = new AssetCollectionService(assetRepository);
        var gitProjectSyncService = new GitProjectSyncService(
            assetCollectionService,
            gitProfileService,
            workspaceService,
            new GitCliProjectSynchronizer(),
            assetRepository);
        var assetTagService = new AssetTagService(assetRepository);
        var transferService = new ManagedAssetTransferService(
            assetRepository,
            workspaceProvisioner,
            new VerifiedManagedFileTransfer());

        return new AppServices(
            normalizedDataDirectory,
            assetDatabasePath,
            readerDatabasePath,
            version,
            clientIdentity,
            assetRepository,
            readerRepository,
            readerHttpClient,
            openWebHttpClient,
            runtimeLog,
            secretStore,
            scanService,
            fingerprintService,
            metadataService,
            readerService,
            workspaceService,
            scanRootService,
            volumeReconciliationService,
            objectStorageProfileService,
            openWebSettingsService,
            gitProfileService,
            openWebPublishingService,
            objectStorageBackupService,
            objectStorageRestoreService,
            objectStorageManagementService,
            assetCollectionService,
            gitProjectSyncService,
            assetTagService,
            transferService,
            new LocalDatabaseBackupService(assetDatabasePath, version),
            new LocalDatabaseBackupService(readerDatabasePath, version, "Reader"),
            new LocalStateProtectionService(
                normalizedDataDirectory,
                assetDatabasePath,
                readerDatabasePath,
                version),
            new PendingStateRestoreService(
                normalizedDataDirectory,
                assetDatabasePath,
                readerDatabasePath));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Scan.InitializeAsync(cancellationToken);
        try
        {
            await Reader.InitializeAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReaderAvailable = false;
            ReaderInitializationError = exception.Message;
            RuntimeLog.WriteError(
                "Reader 数据库初始化失败，RSS 功能已在本次运行中禁用",
                exception);
        }
    }

    public static string GetDefaultDataDirectory()
    {
        var configuredDirectory = NormalizeDataDirectoryOverride(
            Environment.GetEnvironmentVariable("CDSI_BEACON_DATA_DIRECTORY"));
        if (configuredDirectory is not null)
        {
            return configuredDirectory;
        }

        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support");
        }

        return Path.Combine(localData, "CDSI");
    }

    internal static string? NormalizeDataDirectoryOverride(string? configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return null;
        }

        var trimmed = configuredDirectory.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new InvalidOperationException(
                "CDSI_BEACON_DATA_DIRECTORY 必须是绝对路径。");
        }

        return Path.GetFullPath(trimmed);
    }

    public static string GetApplicationVersion()
    {
        var assembly = typeof(AppServices).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+', 2)[0];
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReaderRepository.Dispose();
        _readerHttpClient.Dispose();
        _openWebHttpClient.Dispose();
        if (SecretStore is IDisposable disposableSecretStore)
        {
            disposableSecretStore.Dispose();
        }
    }
}
