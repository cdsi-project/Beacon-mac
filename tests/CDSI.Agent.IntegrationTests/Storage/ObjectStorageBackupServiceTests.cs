using System.Security.Cryptography;
using System.Text;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Infrastructure.Fingerprints;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Storage;

public sealed class ObjectStorageBackupServiceTests
{
    [Fact]
    public async Task BackupAsync_UploadsVerifiesAndKeepsTheLocalFile()
    {
        await using var fixture = await BackupFixture.CreateAsync("source-content");
        var progress = new RecordingProgress<ObjectStorageBackupProgress>();

        var result = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(fixture.AssetId, fixture.SourcePath)],
            fixture.ProfileId,
            progress);
        var audit = await fixture.Repository.GetUploadJobAsync(result.JobId);
        var assets = await fixture.Repository.ListAssetsAsync(100);

        Assert.Equal(UploadJobStatus.Completed, result.Status);
        Assert.Equal(1, fixture.Adapter.UploadCalls);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.Equal("source-content", await File.ReadAllTextAsync(fixture.SourcePath));
        Assert.Equal(UploadItemStatus.Completed, Assert.Single(audit!.Items).Status);
        Assert.Equal(
            $"assets/{fixture.AssetId:N}/source.txt",
            Assert.Single(result.Items).ObjectKey);
        Assert.True(Assert.Single(assets).HasHealthyObjectStorageBackup);
        Assert.Equal(
            new FileInfo(fixture.SourcePath).Length,
            progress.Values.Max(item => item.NetworkTransferredBytes));
    }

    [Fact]
    public async Task BackupAsync_UsesTheRequestedObjectNameAndKeepsTheLocalFile()
    {
        await using var fixture = await BackupFixture.CreateAsync("custom-name-content");

        var result = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(
                fixture.AssetId,
                fixture.SourcePath,
                "发布版-成片.txt")],
            fixture.ProfileId);
        var item = Assert.Single(result.Items);

        Assert.Equal(UploadJobStatus.Completed, result.Status);
        Assert.Equal(
            $"assets/{fixture.AssetId:N}/发布版-成片.txt",
            item.ObjectKey);
        Assert.Equal("custom-name-content", await File.ReadAllTextAsync(fixture.SourcePath));
    }

    [Fact]
    public async Task BackupAsync_WithObjectDirectory_UsesCollectionNameAndOriginalFilename()
    {
        await using var fixture = await BackupFixture.CreateAsync("collection-content");

        var result = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(
                fixture.AssetId,
                fixture.SourcePath,
                ObjectName: "source.txt",
                ObjectDirectory: "第一期视频")],
            fixture.ProfileId);
        var item = Assert.Single(result.Items);

        Assert.Equal(UploadJobStatus.Completed, result.Status);
        Assert.Equal("第一期视频/source.txt", item.ObjectKey);
        Assert.Equal("collection-content", await File.ReadAllTextAsync(fixture.SourcePath));
    }

    [Fact]
    public async Task BackupAsync_WhenCollectionContainsDuplicateFilenames_DoesNotUpload()
    {
        await using var fixture = await BackupFixture.CreateAsync("first-content");
        var secondPath = Path.Combine(
            fixture.Directory.Path,
            "Other",
            "source.txt");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
        await File.WriteAllTextAsync(secondPath, "second-content");
        var secondInfo = new FileInfo(secondPath);
        secondInfo.Refresh();
        var secondFile = new DiscoveredFile(
            secondPath,
            secondInfo.Name,
            secondInfo.Extension,
            "text/plain",
            secondInfo.Length,
            secondInfo.CreationTimeUtc,
            secondInfo.LastWriteTimeUtc);
        var deviceId = await fixture.Repository.GetOrCreateDeviceIdAsync();
        var secondAsset = Assert.Single(
            await fixture.Repository.RegisterLocalFilesAsync(
                deviceId,
                [secondFile],
                DateTimeOffset.UtcNow));

        var result = await fixture.Service.BackupAsync(
            [
                new ObjectStorageBackupRequest(
                    fixture.AssetId,
                    fixture.SourcePath,
                    ObjectName: "source.txt",
                    ObjectDirectory: "第一期视频"),
                new ObjectStorageBackupRequest(
                    secondAsset.AssetId,
                    secondPath,
                    ObjectName: "source.txt",
                    ObjectDirectory: "第一期视频")
            ],
            fixture.ProfileId);

        Assert.Equal(UploadJobStatus.Failed, result.Status);
        Assert.Equal(0, fixture.Adapter.UploadCalls);
        Assert.All(
            result.Items,
            item => Assert.Contains("同一个 OSS 对象", item.ErrorMessage));
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task BackupAsync_WhenObjectNameContainsAPath_DoesNotUpload()
    {
        await using var fixture = await BackupFixture.CreateAsync("invalid-name-content");

        var result = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(
                fixture.AssetId,
                fixture.SourcePath,
                "folder/renamed.txt")],
            fixture.ProfileId);
        var item = Assert.Single(result.Items);

        Assert.Equal(UploadJobStatus.Failed, result.Status);
        Assert.Equal(0, fixture.Adapter.UploadCalls);
        Assert.Contains("路径分隔符", item.ErrorMessage);
        Assert.True(File.Exists(fixture.SourcePath));
    }

    [Fact]
    public async Task BackupAsync_WhenMatchingObjectExists_IsIdempotent()
    {
        await using var fixture = await BackupFixture.CreateAsync("same-content");
        fixture.Adapter.StoredObject = new ObjectStorageObjectInfo(
            "ignored-until-requested",
            new FileInfo(fixture.SourcePath).Length,
            ComputeSha256("same-content"),
            "existing-etag",
            DateTimeOffset.UtcNow);
        var progress = new RecordingProgress<ObjectStorageBackupProgress>();

        var result = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(fixture.AssetId, fixture.SourcePath)],
            fixture.ProfileId,
            progress);

        Assert.Equal(UploadJobStatus.Completed, result.Status);
        Assert.Equal(0, fixture.Adapter.UploadCalls);
        Assert.True(File.Exists(fixture.SourcePath));
        Assert.All(progress.Values, item => Assert.Equal(0, item.NetworkTransferredBytes));
    }

    [Fact]
    public async Task BackupAsync_WhenObjectConflicts_DoesNotOverwriteIt()
    {
        await using var fixture = await BackupFixture.CreateAsync("local-content");
        fixture.Adapter.StoredObject = new ObjectStorageObjectInfo(
            "existing",
            new FileInfo(fixture.SourcePath).Length,
            new string('f', 64),
            "foreign-etag",
            DateTimeOffset.UtcNow);

        var result = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(fixture.AssetId, fixture.SourcePath)],
            fixture.ProfileId);

        Assert.Equal(UploadJobStatus.Failed, result.Status);
        Assert.Equal(0, fixture.Adapter.UploadCalls);
        Assert.Contains("避免覆盖", Assert.Single(result.Items).ErrorMessage);
        Assert.True(File.Exists(fixture.SourcePath));
    }

    [Fact]
    public async Task BackupAsync_RetriesWithThePersistedMultipartSession()
    {
        await using var fixture = await BackupFixture.CreateAsync("resume-content");
        fixture.Adapter.Failure = FakeFailure.AfterCheckpoint;

        var first = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(fixture.AssetId, fixture.SourcePath)],
            fixture.ProfileId);
        fixture.Adapter.Failure = FakeFailure.None;
        var second = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(fixture.AssetId, fixture.SourcePath)],
            fixture.ProfileId);
        var session = await fixture.Repository.GetMultipartUploadSessionAsync(
            fixture.ProfileId,
            Assert.Single(second.Items).ObjectKey);

        Assert.Equal(UploadJobStatus.Failed, first.Status);
        Assert.Equal(UploadJobStatus.Completed, second.Status);
        Assert.NotNull(fixture.Adapter.LastReceivedSession);
        Assert.Null(session);
        Assert.True(File.Exists(fixture.SourcePath));
    }

    [Fact]
    public async Task BackupAsync_WhenPostUploadVerificationDiffers_DoesNotMarkHealthy()
    {
        await using var fixture = await BackupFixture.CreateAsync("verify-content");
        fixture.Adapter.ReturnMismatchedObjectAfterUpload = true;

        var result = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(fixture.AssetId, fixture.SourcePath)],
            fixture.ProfileId);
        var location = await fixture.Repository.GetObjectStorageLocationAsync(
            fixture.AssetId,
            fixture.ProfileId,
            Assert.Single(result.Items).ObjectKey);

        Assert.Equal(UploadJobStatus.Failed, result.Status);
        Assert.Equal(StorageVerificationStatus.ChecksumMismatch, location?.Status);
        Assert.True(File.Exists(fixture.SourcePath));
    }

    [Fact]
    public async Task BackupAsync_WhenCancelled_PreservesTheMultipartSession()
    {
        await using var fixture = await BackupFixture.CreateAsync("cancel-content");
        fixture.Adapter.Failure = FakeFailure.CancelAfterCheckpoint;

        var result = await fixture.Service.BackupAsync(
            [new ObjectStorageBackupRequest(fixture.AssetId, fixture.SourcePath)],
            fixture.ProfileId);
        var session = await fixture.Repository.GetMultipartUploadSessionAsync(
            fixture.ProfileId,
            Assert.Single(result.Items).ObjectKey);

        Assert.Equal(UploadJobStatus.Cancelled, result.Status);
        Assert.NotNull(session);
        Assert.True(File.Exists(fixture.SourcePath));
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            Values.Add(value);
        }
    }

    private static string ComputeSha256(string content)
    {
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private enum FakeFailure
    {
        None,
        AfterCheckpoint,
        CancelAfterCheckpoint
    }

    private sealed class FakeObjectStorageAdapter : IObjectStorageAdapter
    {
        public ObjectStorageProvider Provider => ObjectStorageProvider.AliyunOss;

        public int UploadCalls { get; private set; }

        public ObjectStorageObjectInfo? StoredObject { get; set; }

        public MultipartUploadSession? LastReceivedSession { get; private set; }

        public FakeFailure Failure { get; set; }

        public bool ReturnMismatchedObjectAfterUpload { get; set; }

        public Task<ObjectStorageObjectInfo?> StatAsync(
            ObjectStorageConnection connection,
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(StoredObject is null
                ? null
                : StoredObject with { ObjectKey = objectKey });
        }

        public async Task<ObjectStorageTransferResult> UploadAsync(
            ObjectStorageTransferRequest request,
            Func<MultipartUploadSession, CancellationToken, Task> saveCheckpoint,
            IProgress<ObjectStorageTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            LastReceivedSession = request.Session;
            var session = request.Session ?? new MultipartUploadSession(
                request.Connection.Profile.Id,
                request.AssetId,
                request.ObjectKey,
                request.SourcePath,
                "test-upload-id",
                16 * 1024 * 1024,
                request.Size,
                request.ModifiedAt,
                [],
                DateTimeOffset.UtcNow);
            await saveCheckpoint(session, cancellationToken);
            if (Failure == FakeFailure.AfterCheckpoint)
            {
                throw new IOException("Simulated network failure.");
            }

            if (Failure == FakeFailure.CancelAfterCheckpoint)
            {
                throw new OperationCanceledException("Simulated cancellation.");
            }

            progress?.Report(new ObjectStorageTransferProgress(
                request.Size,
                request.Size,
                request.Size,
                1,
                1,
                "测试上传完成"));
            StoredObject = new ObjectStorageObjectInfo(
                request.ObjectKey,
                request.Size,
                ReturnMismatchedObjectAfterUpload
                    ? new string('0', 64)
                    : request.Sha256,
                "test-etag",
                DateTimeOffset.UtcNow);
            return new ObjectStorageTransferResult(StoredObject, Uploaded: true);
        }

        public Task<ObjectStorageDownloadResult> DownloadAsync(
            ObjectStorageConnection connection,
            string objectKey,
            Stream destination,
            IProgress<ObjectStorageDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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

        public Task<string?> RetrieveAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryGetValue(key, out var secret);
            return Task.FromResult(secret);
        }

        public Task<bool> ExistsAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_values.ContainsKey(key));
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

    private sealed class BackupFixture : IAsyncDisposable
    {
        private BackupFixture(
            TestDirectory directory,
            SqliteAssetRepository repository,
            ObjectStorageBackupService service,
            FakeObjectStorageAdapter adapter,
            Guid profileId,
            Guid assetId,
            string sourcePath)
        {
            Directory = directory;
            Repository = repository;
            Service = service;
            Adapter = adapter;
            ProfileId = profileId;
            AssetId = assetId;
            SourcePath = sourcePath;
        }

        public TestDirectory Directory { get; }

        public SqliteAssetRepository Repository { get; }

        public ObjectStorageBackupService Service { get; }

        public FakeObjectStorageAdapter Adapter { get; }

        public Guid ProfileId { get; }

        public Guid AssetId { get; }

        public string SourcePath { get; }

        public static async Task<BackupFixture> CreateAsync(string content)
        {
            var directory = new TestDirectory();
            var repository = new SqliteAssetRepository(
                Path.Combine(directory.Path, "State", "cdsi.db"));
            await repository.InitializeAsync();
            var sourcePath = Path.Combine(directory.Path, "Assets", "source.txt");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
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
            var adapter = new FakeObjectStorageAdapter();
            var service = new ObjectStorageBackupService(
                repository,
                repository,
                profileService,
                new Sha256FileFingerprintService(),
                [adapter]);
            return new BackupFixture(
                directory,
                repository,
                service,
                adapter,
                configured.Profile.Id,
                registered.AssetId,
                sourcePath);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
