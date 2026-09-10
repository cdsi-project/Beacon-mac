using CDSI.Agent.Core.Storage;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class SqliteStorageProfileRepositoryTests
{
    [Fact]
    public async Task StorageProfiles_PersistConfigurationWithoutASecretColumn()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var profile = new ObjectStorageProfile(
            Guid.NewGuid(),
            "主 OSS",
            ObjectStorageProvider.AliyunOss,
            "oss-cn-hangzhou.aliyuncs.com",
            "cdsi-assets",
            "cn-hangzhou",
            true,
            "test-access-key-id",
            now,
            now);

        await repository.SaveStorageProfileAsync(profile);
        var loaded = Assert.Single(await repository.ListStorageProfilesAsync());

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info(storage_profiles);";
        var columnNames = new List<string>();
        await using (var reader = await columnsCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columnNames.Add(reader.GetString(1));
            }
        }

        Assert.Equal(profile, loaded);
        Assert.DoesNotContain(
            columnNames,
            name => name.Contains("secret", StringComparison.OrdinalIgnoreCase));

        Assert.True(await repository.DeleteStorageProfileAsync(profile.Id));
        Assert.Empty(await repository.ListStorageProfilesAsync());

        SqliteConnection.ClearAllPools();
    }
}
