using System.Security.Cryptography;
using System.Text;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Infrastructure.FileSystem;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Storage;

public sealed class ObjectStorageRestoreServiceTests
{
    [Fact]
    public async Task RestoreAsync_DownloadsVerifiesAndRegistersTheSameAsset()
    {
        await using var fixture = await RestoreFixture.CreateAsync("cloud-content");

        var result = await fixture.RestoreAsync();
        var audit = await fixture.Repository.GetRestoreJobAsync(result.JobId);
        var registered = await fixture.Repository.GetLocalAssetTransferSourceAsync(
            fixture.AssetId,
            fixture.DeviceId,
            fixture.TargetPath);

        Assert.Equal(RestoreJobStatus.Completed, result.Status);
        Assert.Equal(1, fixture.Adapter.DownloadCalls);
        Assert.Equal("cloud-content", await File.ReadAllTextAsync(fixture.TargetPath));
        Assert.Equal(fixture.AssetId, registered!.AssetId);
        Assert.Equal(RestoreItemStatus.Completed, Assert.Single(audit!.Items).Status);
        Assert.Equal(fixture.Hash, Assert.Single(audit.Items).Sha256);
    }

    [Fact]
    public async Task RestoreAsync_ExistingDifferentFileIsNeverOverwritten()
    {
        await using var fixture = await RestoreFixture.CreateAsync("cloud-content");
        await File.WriteAllTextAsync(fixture.TargetPath, "local-content");

        var result = await fixture.RestoreAsync();

        Assert.Equal(RestoreJobStatus.Failed, result.Status);
        Assert.Equal(0, fixture.Adapter.DownloadCalls);
        Assert.Equal("local-content", await File.ReadAllTextAsync(fixture.TargetPath));
        Assert.Contains("未覆盖", Assert.Single(result.Items).ErrorMessage);
    }

    [Fact]
    public async Task RestoreAsync_ExistingMatchingFileIsReusedWithoutDownload()
    {
        await using var fixture = await RestoreFixture.CreateAsync("cloud-content");
        await File.WriteAllTextAsync(fixture.TargetPath, "cloud-content");

        var result = await fixture.RestoreAsync();
        var registered = await fixture.Repository.GetLocalAssetTransferSourceAsync(
            fixture.AssetId,
            fixture.DeviceId,
            fixture.TargetPath);

        Assert.Equal(RestoreJobStatus.Completed, result.Status);
        Assert.Equal(0, fixture.Adapter.DownloadCalls);
        Assert.NotNull(registered);
        Assert.Equal(0, Assert.Single(result.Items).DownloadedBytes);
    }

    [Fact]
    public async Task RestoreAsync_HashMismatchLeavesNoTargetOrPartialFile()
    {
        await using var fixture = await RestoreFixture.CreateAsync("cloud-content");
        fixture.Adapter.DownloadContent = Encoding.UTF8.GetBytes("tampered-data");

        var result = await fixture.RestoreAsync();

        Assert.Equal(RestoreJobStatus.Failed, result.Status);
        Assert.False(File.Exists(fixture.TargetPath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.RestoreDirectory,
            "*.cdsi-part",
            SearchOption.AllDirectories));
        Assert.Contains("校验失败", Assert.Single(result.Items).ErrorMessage);
        var source = Assert.Single(Assert.Single(
            await fixture.Service.ListCandidatesAsync([fixture.AssetId])).Sources);
        Assert.Equal(
            StorageVerificationStatus.Unverified,
            source.Source.Location.Status);
    }

    [Fact]
    public async Task RestoreAsync_ObjectChangedAfterStatUpdatesVerificationStatus()
    {
        await using var fixture = await RestoreFixture.CreateAsync("cloud-content");
        fixture.Adapter.DownloadObject = fixture.Adapter.StoredObject! with
        {
            Sha256 = new string('b', 64)
        };

        var result = await fixture.RestoreAsync();
        var source = Assert.Single(Assert.Single(
            await fixture.Service.ListCandidatesAsync([fixture.AssetId])).Sources);

        Assert.Equal(RestoreJobStatus.Failed, result.Status);
        Assert.False(File.Exists(fixture.TargetPath));
        Assert.Equal(
            StorageVerificationStatus.ChecksumMismatch,
            source.Source.Location.Status);
    }

    [Fact]
    public async Task ListCandidatesAsync_ReturnsTheRegisteredHealthyLocation()
    {
        await using var fixture = await RestoreFixture.CreateAsync("cloud-content");

        var candidates = await fixture.Service.ListCandidatesAsync([fixture.AssetId]);
        var source = Assert.Single(Assert.Single(candidates).Sources);

        Assert.True(source.HasStoredSecret);
        Assert.Equal(fixture.LocationId, source.Source.Location.Id);
        Assert.Equal(StorageVerificationStatus.Healthy, source.Source.Location.Status);
    }

    private sealed class RestoreFixture : IAsyncDisposable
    {
        private RestoreFixture(
            TestDirectory directory,
            SqliteAssetRepository repository,
            ObjectStorageRestoreService service,
            FakeObjectStorageAdapter adapter,
            Guid assetId,
            Guid locationId,
            string deviceId,
            string restoreDirectory,
            string targetPath,
            string hash)
        {
            Directory = directory;
            Repository = repository;
            Service = service;
            Adapter = adapter;
            AssetId = assetId;
            LocationId = locationId;
            DeviceId = deviceId;
            RestoreDirectory = restoreDirectory;
            TargetPath = targetPath;
            Hash = hash;
        }

        public TestDirectory Directory { get; }
        public SqliteAssetRepository Repository { get; }
        public ObjectStorageRestoreService Service { get; }
        public FakeObjectStorageAdapter Adapter { get; }
        public Guid AssetId { get; }
        public Guid LocationId { get; }
        public string DeviceId { get; }
        public string RestoreDirectory { get; }
        public string TargetPath { get; }
        public string Hash { get; }

        public static async Task<RestoreFixture> CreateAsync(string content)
        {
            var directory = new TestDirectory();
            var repository = new SqliteAssetRepository(
                Path.Combine(directory.Path, "State", "cdsi.db"));
            await repository.InitializeAsync();
            var sourceDirectory = Path.Combine(directory.Path, "Source");
            System.IO.Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "article.txt");
            await File.WriteAllTextAsync(sourcePath, content);
            var info = new FileInfo(sourcePath);
            info.Refresh();
            var discovered = new DiscoveredFile(
                sourcePath,
                info.Name,
                info.Extension,
                "text/plain",
                info.Length,
                info.CreationTimeUtc,
                info.LastWriteTimeUtc);
            var deviceId = await repository.GetOrCreateDeviceIdAsync();
            var registered = Assert.Single(await repository.RegisterLocalFilesAsync(
                deviceId,
                [discovered],
                DateTimeOffset.UtcNow));
            var bytes = Encoding.UTF8.GetBytes(content);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            Assert.True(await repository.SaveSha256Async(
                registered.AssetId,
                info.Length,
                info.LastWriteTimeUtc,
                hash));

            var secretStore = new InMemorySecretStore();
            var profileService = new ObjectStorageProfileService(
                repository,
                secretStore);
            var profile = await profileService.SaveAsync(
                new SaveObjectStorageProfileRequest(
                    null,
                    "测试 OSS",
                    "oss-cn-hangzhou.aliyuncs.com",
                    "cdsi-test-assets",
                    "cn-hangzhou",
                    true,
                    "test-access-key-id",
                    "test-access-key-secret"));
            var locationId = Guid.NewGuid();
            var objectKey = $"assets/{registered.AssetId:N}/article.txt";
            var now = DateTimeOffset.UtcNow;
            await repository.SaveObjectStorageLocationAsync(
                new ObjectStorageLocation(
                    locationId,
                    registered.AssetId,
                    profile.Profile.Id,
                    objectKey,
                    StorageVerificationStatus.Healthy,
                    bytes.LongLength,
                    hash,
                    "test-etag",
                    now,
                    now,
                    now));
            var adapter = new FakeObjectStorageAdapter(
                bytes,
                new ObjectStorageObjectInfo(
                    objectKey,
                    bytes.LongLength,
                    hash,
                    "test-etag",
                    now));
            var service = new ObjectStorageRestoreService(
                repository,
                repository,
                repository,
                profileService,
                new WorkspaceProvisioner(),
                [adapter]);
            var restoreDirectory = Path.Combine(directory.Path, "Restored");
            System.IO.Directory.CreateDirectory(restoreDirectory);
            return new RestoreFixture(
                directory,
                repository,
                service,
                adapter,
                registered.AssetId,
                locationId,
                deviceId,
                restoreDirectory,
                Path.Combine(restoreDirectory, "article.txt"),
                hash);
        }

        public Task<ObjectStorageRestoreResult> RestoreAsync()
        {
            return Service.RestoreAsync(
                [new ObjectStorageRestoreRequest(AssetId, LocationId)],
                new ObjectStorageRestoreDestination(
                    ObjectStorageRestoreDestinationKind.SelectedDirectory,
                    RestoreDirectory));
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeObjectStorageAdapter : IObjectStorageAdapter
    {
        public FakeObjectStorageAdapter(
            byte[] downloadContent,
            ObjectStorageObjectInfo storedObject)
        {
            DownloadContent = downloadContent;
            StoredObject = storedObject;
            DownloadObject = storedObject;
        }

        public ObjectStorageProvider Provider => ObjectStorageProvider.AliyunOss;
        public byte[] DownloadContent { get; set; }
        public ObjectStorageObjectInfo? StoredObject { get; set; }
        public ObjectStorageObjectInfo DownloadObject { get; set; }
        public int DownloadCalls { get; private set; }

        public Task<ObjectStorageObjectInfo?> StatAsync(
            ObjectStorageConnection connection,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(StoredObject);
        }

        public Task<ObjectStorageTransferResult> UploadAsync(
            ObjectStorageTransferRequest request,
            Func<MultipartUploadSession, CancellationToken, Task> saveCheckpoint,
            IProgress<ObjectStorageTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async Task<ObjectStorageDownloadResult> DownloadAsync(
            ObjectStorageConnection connection,
            string objectKey,
            Stream destination,
            IProgress<ObjectStorageDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            await destination.WriteAsync(DownloadContent, cancellationToken);
            progress?.Report(new ObjectStorageDownloadProgress(
                DownloadContent.LongLength,
                DownloadContent.LongLength,
                "下载完成"));
            return new ObjectStorageDownloadResult(
                DownloadObject,
                DownloadContent.LongLength);
        }

        public Task AbortMultipartUploadAsync(
            ObjectStorageConnection connection,
            MultipartUploadSession session,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task StoreAsync(
            string key,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = secret;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.ContainsKey(key));
        }

        public Task<string?> RetrieveAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryGetValue(key, out var secret);
            return Task.FromResult(secret);
        }

        public Task DeleteAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
