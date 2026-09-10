using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Infrastructure.Persistence;
using CDSI.Agent.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Storage;

public sealed class ObjectStorageProfileServiceTests
{
    [Fact]
    public async Task SaveListUpdateAndDelete_KeepSecretsOutsideSqlite()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "State", "cdsi.db");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var secrets = new InMemorySecretStore();
        var service = new ObjectStorageProfileService(repository, secrets);
        const string secret = "never-write-this-secret-to-sqlite";

        var created = await service.SaveAsync(new SaveObjectStorageProfileRequest(
            null,
            "主 OSS",
            "oss-cn-hangzhou.aliyuncs.com",
            "cdsi-assets",
            "cn-hangzhou",
            true,
            "test-access-key-id",
            secret));
        var listed = Assert.Single(await service.ListAsync());
        var updated = await service.SaveAsync(new SaveObjectStorageProfileRequest(
            created.Profile.Id,
            "主 OSS 归档",
            "https://oss-cn-hangzhou.aliyuncs.com",
            "cdsi-assets",
            "cn-hangzhou",
            true,
            "updated-access-key-id",
            null));

        SqliteConnection.ClearAllPools();
        Assert.True(created.HasStoredSecret);
        Assert.True(listed.HasStoredSecret);
        Assert.Equal("主 OSS 归档", updated.Profile.DisplayName);
        Assert.Equal("oss-cn-hangzhou.aliyuncs.com", updated.Profile.Endpoint);
        Assert.Equal(secret, secrets.GetOnlySecret());

        var databaseBytes = await File.ReadAllBytesAsync(databasePath);
        Assert.DoesNotContain(
            secret,
            System.Text.Encoding.UTF8.GetString(databaseBytes),
            StringComparison.Ordinal);

        await service.DeleteAsync(created.Profile.Id);
        var remaining = await service.ListAsync();
        SqliteConnection.ClearAllPools();

        Assert.Empty(remaining);
        Assert.Empty(secrets.Values);
    }

    [Theory]
    [InlineData("UPPERCASE")]
    [InlineData("-starts-with-hyphen")]
    [InlineData("ends-with-hyphen-")]
    [InlineData("ab")]
    public async Task SaveAsync_RejectsInvalidBucketNames(string bucketName)
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var service = new ObjectStorageProfileService(
            repository,
            new InMemorySecretStore());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveAsync(new SaveObjectStorageProfileRequest(
                null,
                "测试",
                "oss-cn-hangzhou.aliyuncs.com",
                bucketName,
                null,
                true,
                "access-key-id",
                "access-key-secret")));

        Assert.Contains("Bucket", exception.Message);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SaveAsync_RequiresASecretForANewProfile()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var service = new ObjectStorageProfileService(
            repository,
            new InMemorySecretStore());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveAsync(new SaveObjectStorageProfileRequest(
                null,
                "测试",
                "oss-cn-hangzhou.aliyuncs.com",
                "valid-bucket",
                null,
                true,
                "access-key-id",
                null)));

        Assert.Contains("AccessKey Secret", exception.Message);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SaveAsync_WhenInitialDatabaseSaveFails_RemovesTheTemporarySecret()
    {
        var secrets = new InMemorySecretStore();
        var service = new ObjectStorageProfileService(
            new FailingStorageProfileRepository(),
            secrets);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(new SaveObjectStorageProfileRequest(
                null,
                "测试",
                "oss-cn-hangzhou.aliyuncs.com",
                "valid-bucket",
                null,
                true,
                "access-key-id",
                "access-key-secret")));

        Assert.Empty(secrets.Values);
    }

    [Theory]
    [InlineData(
        ObjectStorageProvider.QiniuKodo,
        "s3.cn-east-1.qiniucs.com",
        "cn-east-1")]
    [InlineData(
        ObjectStorageProvider.TencentCos,
        "cos.ap-guangzhou.myqcloud.com",
        "ap-guangzhou")]
    public async Task SaveAsync_PersistsS3CompatibleProviderAndRequiresRegion(
        ObjectStorageProvider provider,
        string endpoint,
        string region)
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var service = new ObjectStorageProfileService(
            repository,
            new InMemorySecretStore());

        var missingRegion = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveAsync(new SaveObjectStorageProfileRequest(
                null,
                "对象存储备份",
                endpoint,
                "cdsi-assets",
                null,
                true,
                "access-key-id",
                "access-key-secret",
                provider)));
        Assert.Contains("Region ID", missingRegion.Message);

        var saved = await service.SaveAsync(new SaveObjectStorageProfileRequest(
            null,
            "对象存储备份",
            endpoint,
            "cdsi-assets",
            region,
            true,
            "access-key-id",
            "access-key-secret",
            provider));

        Assert.Equal(provider, saved.Profile.Provider);
        Assert.Equal(region, saved.Profile.Region);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SaveAsync_WhenProviderChanges_RequiresNewSecret()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        await repository.InitializeAsync();
        var service = new ObjectStorageProfileService(
            repository,
            new InMemorySecretStore());
        var existing = await service.SaveAsync(new SaveObjectStorageProfileRequest(
            null,
            "主备份",
            "oss-cn-hangzhou.aliyuncs.com",
            "cdsi-assets",
            "cn-hangzhou",
            true,
            "aliyun-key",
            "aliyun-secret"));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.SaveAsync(new SaveObjectStorageProfileRequest(
                existing.Profile.Id,
                "七牛备份",
                "s3.cn-east-1.qiniucs.com",
                "cdsi-assets",
                "cn-east-1",
                true,
                "qiniu-key",
                null,
                ObjectStorageProvider.QiniuKodo)));

        Assert.Contains("重新填写 AccessKey Secret", exception.Message);
        SqliteConnection.ClearAllPools();
    }

    private sealed class FailingStorageProfileRepository : IStorageProfileRepository
    {
        public Task<IReadOnlyList<ObjectStorageProfile>> ListStorageProfilesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ObjectStorageProfile>>([]);
        }

        public Task SaveStorageProfileAsync(
            ObjectStorageProfile profile,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Expected database failure.");
        }

        public Task<bool> DeleteStorageProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }
    private sealed class InMemorySecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } =
            new(StringComparer.Ordinal);

        public Task StoreAsync(
            string key,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[key] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> RetrieveAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.TryGetValue(key, out var secret);
            return Task.FromResult(secret);
        }

        public Task<bool> ExistsAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.ContainsKey(key));
        }

        public Task DeleteAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.Remove(key);
            return Task.CompletedTask;
        }

        public string GetOnlySecret()
        {
            return Assert.Single(Values).Value;
        }
    }
}
