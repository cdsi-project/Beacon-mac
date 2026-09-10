using CDSI.Agent.Core.Git;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class SqliteGitProfileRepositoryTests
{
    [Fact]
    public async Task Version21Profile_MigratesToPasswordAuthentication()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                CREATE TABLE git_profiles (
                    id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    repository_url TEXT NOT NULL COLLATE NOCASE,
                    account_name TEXT NOT NULL,
                    default_branch TEXT NOT NULL,
                    is_default INTEGER NOT NULL CHECK(is_default IN (0, 1)),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(provider, repository_url)
                );
                CREATE UNIQUE INDEX ux_git_profiles_default
                ON git_profiles(is_default)
                WHERE is_default = 1;
                CREATE TABLE scan_roots (
                    file_type_filter TEXT NOT NULL DEFAULT 'All',
                    extension_whitelist_json TEXT NOT NULL DEFAULT '[]'
                );
                INSERT INTO schema_migrations(version, applied_at)
                VALUES(21, $now);
                INSERT INTO git_profiles(
                    id, display_name, provider, repository_url, account_name,
                    default_branch, is_default, created_at, updated_at)
                VALUES(
                    $id, '旧 GitHub', 'GitHub',
                    'https://github.com/owner/repository.git', 'owner',
                    'main', 1, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var profile = Assert.Single(await repository.ListGitProfilesAsync());

        Assert.Equal(GitAuthenticationMethod.Password, profile.AuthenticationMethod);
        Assert.Equal("owner", profile.Username);
        Assert.Null(profile.SshPublicKeyPath);

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(28, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }

        SqliteConnection.ClearAllPools();
    }
}
