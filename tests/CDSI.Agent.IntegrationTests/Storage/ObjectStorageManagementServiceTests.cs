using System.Security.Cryptography;
using System.Text;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Storage;

public sealed class ObjectStorageManagementServiceTests
{
    [Fact]
    public async Task RenameAsync_CopiesVerifiesUpdatesMappingThenDeletesOldObject()
    {
        await using var fixture = await ManagementFixture.CreateAsync("video-content");

        var result = await fixture.Service.RenameAsync(
            fixture.LocationId,
            "成片.mp4");
        var backups = await fixture.Repository.ListManagedObjectStorageBackupsAsync();
        var backup = Assert.Single(backups);

        Assert.True(result.OldObjectDeleted);
        Assert.Null(result.WarningMessage);
        Assert.Equal("项目一/成片.mp4", backup.Location.ObjectKey);
        Assert.False(fixture.Adapter.Objects.ContainsKey("项目一/原片.mp4"));
        Assert.True(fixture.Adapter.Objects.ContainsKey("项目一/成片.mp4"));
        Assert.True(
            fixture.Adapter.Operations.IndexOf("copy:项目一/成片.mp4") <
            fixture.Adapter.Operations.IndexOf("delete:项目一/原片.mp4"));
    }

    [Fact]
    public async Task ListAsync_IncludesTheAvailableLocalFilePath()
    {
        await using var fixture = await ManagementFixture.CreateAsync("video-content");

        var backup = Assert.Single(await fixture.Service.ListAsync());

        Assert.Equal(fixture.SourcePath, backup.Source.LocalPath);
        Assert.Equal(["项目一"], backup.Source.ProjectNames);
    }

    [Fact]
    public async Task RenameAsync_WhenCopiedObjectFailsVerification_KeepsOldObjectAndMapping()
    {
        await using var fixture = await ManagementFixture.CreateAsync("video-content");
        fixture.Adapter.ReturnMismatchedCopy = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.RenameAsync(
            fixture.LocationId,
            "错误成片.mp4"));
        var backup = Assert.Single(
            await fixture.Repository.ListManagedObjectStorageBackupsAsync());

        Assert.Equal("项目一/原片.mp4", backup.Location.ObjectKey);
        Assert.True(fixture.Adapter.Objects.ContainsKey("项目一/原片.mp4"));
        Assert.False(fixture.Adapter.Objects.ContainsKey("项目一/错误成片.mp4"));
    }

    [Fact]
    public async Task DeleteAsync_RemovesRemoteObjectAndLocalMapping()
    {
        await using var fixture = await ManagementFixture.CreateAsync("video-content");

        await fixture.Service.DeleteAsync(fixture.LocationId);

        Assert.Empty(await fixture.Repository.ListManagedObjectStorageBackupsAsync());
        Assert.Empty(fixture.Adapter.Objects);
    }

    [Fact]
    public async Task DeleteAsync_WhenRemoteObjectDoesNotMatch_KeepsObjectAndMapping()
    {
        await using var fixture = await ManagementFixture.CreateAsync("video-content");
        var current = fixture.Adapter.Objects["项目一/原片.mp4"];
        fixture.Adapter.Objects["项目一/原片.mp4"] = current with
        {
            Sha256 = new string('0', 64)
        };

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.DeleteAsync(fixture.LocationId));

        Assert.Single(await fixture.Repository.ListManagedObjectStorageBackupsAsync());
        Assert.True(fixture.Adapter.Objects.ContainsKey("项目一/原片.mp4"));
    }

    private sealed class ManagementFixture : IAsyncDisposable
    {
        private readonly TestDirectory _directory;

        private ManagementFixture(
            TestDirectory directory,
            SqliteAssetRepository repository,
            ObjectStorageManagementService service,
            FakeObjectStorageAdapter adapter,
            Guid locationId,
            string sourcePath)
        {
            _directory = directory;
            Repository = repository;
            Service = service;
            Adapter = adapter;
            LocationId = locationId;
            SourcePath = sourcePath;
        }

        public SqliteAssetRepository Repository { get; }
        public ObjectStorageManagementService Service { get; }
        public FakeObjectStorageAdapter Adapter { get; }
        public Guid LocationId { get; }
        public string SourcePath { get; }

        public static async Task<ManagementFixture> CreateAsync(string content)
        {
            var directory = new TestDirectory();
            var repository = new SqliteAssetRepository(
                Path.Combine(directory.Path, "State", "cdsi.db"));
            await repository.InitializeAsync();
            var sourceDirectory = Path.Combine(directory.Path, "Source");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "原片.mp4");
            await File.WriteAllTextAsync(sourcePath, content);
            var info = new FileInfo(sourcePath);
            info.Refresh();
            var discovered = new DiscoveredFile(
                sourcePath,
                info.Name,
                info.Extension,
                "video/mp4",
                info.Length,
                info.CreationTimeUtc,
                info.LastWriteTimeUtc);
            var deviceId = await repository.GetOrCreateDeviceIdAsync();
            var registered = Assert.Single(await repository.RegisterLocalFilesAsync(
                deviceId,
                [discovered],
                DateTimeOffset.UtcNow));
            var hash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            Assert.True(await repository.SaveSha256Async(
                registered.AssetId,
                info.Length,
                info.LastWriteTimeUtc,
                hash));
            var projectTime = DateTimeOffset.UtcNow;
            var collection = new AssetCollection(
                Guid.NewGuid(),
                "项目一",
                AssetCollectionType.Video,
                projectTime,
                projectTime);
            Assert.True(await repository.CreateAssetCollectionAsync(collection));
            Assert.Equal(1, await repository.AddAssetsToCollectionAsync(
                collection.Id,
                [registered.AssetId],
                projectTime));

            var secretStore = new InMemorySecretStore();
            var profileService = new ObjectStorageProfileService(
                repository,
                secretStore);
            var configured = await profileService.SaveAsync(
                new SaveObjectStorageProfileRequest(
                    null,
                    "测试 OSS",
                    "oss-cn-hangzhou.aliyuncs.com",
                    "cdsi-test-assets",
                    "cn-hangzhou",
                    true,
                    "test-access-key-id",
                    "test-access-key-secret"));
            var objectKey = "项目一/原片.mp4";
            var now = DateTimeOffset.UtcNow;
            var locationId = Guid.NewGuid();
            await repository.SaveObjectStorageLocationAsync(
                new ObjectStorageLocation(
                    locationId,
                    registered.AssetId,
                    configured.Profile.Id,
                    objectKey,
                    StorageVerificationStatus.Healthy,
                    info.Length,
                    hash,
                    "source-etag",
                    now,
                    now,
                    now));
            var adapter = new FakeObjectStorageAdapter();
            adapter.Objects[objectKey] = new ObjectStorageObjectInfo(
                objectKey,
                info.Length,
                hash,
                "source-etag",
                now);
            var service = new ObjectStorageManagementService(
                repository,
                profileService,
                [adapter]);
            return new ManagementFixture(
                directory,
                repository,
                service,
                adapter,
                locationId,
                sourcePath);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            _directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeObjectStorageAdapter : IObjectStorageAdapter
    {
        public ObjectStorageProvider Provider => ObjectStorageProvider.AliyunOss;
        public Dictionary<string, ObjectStorageObjectInfo> Objects { get; } = [];
        public List<string> Operations { get; } = [];
        public bool ReturnMismatchedCopy { get; set; }

        public Task<ObjectStorageObjectInfo?> StatAsync(
            ObjectStorageConnection connection,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Objects.TryGetValue(objectKey, out var value);
            return Task.FromResult(value);
        }

        public Task<ObjectStorageObjectInfo> CopyAsync(
            ObjectStorageCopyRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"copy:{request.DestinationObjectKey}");
            var source = Objects[request.SourceObjectKey];
            var copied = source with
            {
                ObjectKey = request.DestinationObjectKey,
                Sha256 = ReturnMismatchedCopy ? new string('0', 64) : source.Sha256,
                ETag = "copied-etag"
            };
            Objects[request.DestinationObjectKey] = copied;
            return Task.FromResult(copied);
        }

        public Task DeleteAsync(
            ObjectStorageConnection connection,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"delete:{objectKey}");
            Objects.Remove(objectKey);
            return Task.CompletedTask;
        }

        public Task<ObjectStorageTransferResult> UploadAsync(
            ObjectStorageTransferRequest request,
            Func<MultipartUploadSession, CancellationToken, Task> saveCheckpoint,
            IProgress<ObjectStorageTransferProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectStorageDownloadResult> DownloadAsync(
            ObjectStorageConnection connection,
            string objectKey,
            Stream destination,
            IProgress<ObjectStorageDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AbortMultipartUploadAsync(
            ObjectStorageConnection connection,
            MultipartUploadSession session,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public Task StoreAsync(
            string key,
            string secret,
            CancellationToken cancellationToken = default)
        {
            _values[key] = secret;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.ContainsKey(key));

        public Task<string?> RetrieveAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            _values.TryGetValue(key, out var secret);
            return Task.FromResult(secret);
        }

        public Task DeleteAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
