using CDSI.Agent.Application.Git;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Git;

public sealed class GitProfileServiceTests
{
    [Fact]
    public async Task Profiles_SeparatePasswordsFromSqliteAndSupportSshKeys()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var secretStore = new InMemorySecretStore();
        var service = new GitProfileService(repository, secretStore);
        var publicKeyPath = CreateSshKeyPair(directory.Path);

        var github = await service.SaveAsync(new SaveGitProfileRequest(
            null,
            "主 GitHub",
            GitHostingProvider.GitHub,
            "https://github.com/cdsi-project/Beacon.git",
            "main",
            GitAuthenticationMethod.Password,
            "cdsi-project",
            "github-password",
            null,
            IsDefault: false));
        var gitee = await service.SaveAsync(new SaveGitProfileRequest(
            null,
            "码云镜像",
            GitHostingProvider.Gitee,
            "git@gitee.com:cdsi-project/beacon.git",
            "master",
            GitAuthenticationMethod.Ssh,
            null,
            null,
            publicKeyPath,
            IsDefault: true));

        var profiles = await service.ListAsync();
        Assert.Equal(2, profiles.Count);
        Assert.Equal(gitee.Profile.Id, Assert.Single(
            profiles,
            profile => profile.Profile.IsDefault).Profile.Id);
        Assert.True(profiles.Single(
            profile => profile.Profile.Id == github.Profile.Id).HasPassword);
        Assert.False(profiles.Single(
            profile => profile.Profile.Id == gitee.Profile.Id).HasPassword);
        Assert.Equal("github-password", await service.GetPasswordAsync(github.Profile.Id));
        Assert.Null(await service.GetPasswordAsync(gitee.Profile.Id));
        Assert.Equal(publicKeyPath, gitee.Profile.SshPublicKeyPath);

        var updated = await service.SaveAsync(new SaveGitProfileRequest(
            github.Profile.Id,
            "主 GitHub（更新）",
            GitHostingProvider.GitHub,
            github.Profile.RepositoryUrl,
            github.Profile.DefaultBranch,
            GitAuthenticationMethod.Password,
            github.Profile.Username,
            null,
            null,
            IsDefault: false));
        Assert.True(updated.HasPassword);
        Assert.Equal("github-password", await service.GetPasswordAsync(github.Profile.Id));

        github = await service.SaveAsync(new SaveGitProfileRequest(
            github.Profile.Id,
            updated.Profile.DisplayName,
            GitHostingProvider.GitHub,
            "git@github.com:cdsi-project/Beacon.git",
            updated.Profile.DefaultBranch,
            GitAuthenticationMethod.Ssh,
            null,
            null,
            publicKeyPath,
            IsDefault: false));
        Assert.False(github.HasPassword);
        Assert.Null(await service.GetPasswordAsync(github.Profile.Id));
        Assert.False(await secretStore.ExistsAsync(
            $"git-password-{github.Profile.Id:N}"));

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var columnsCommand = connection.CreateCommand();
            columnsCommand.CommandText = "PRAGMA table_info(git_profiles);";
            var columns = new List<string>();
            await using (var reader = await columnsCommand.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }
            }

            Assert.Contains("authentication_method", columns);
            Assert.Contains("ssh_public_key_path", columns);
            Assert.DoesNotContain(
                columns,
                column => column.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    column.Contains("secret", StringComparison.OrdinalIgnoreCase));

            await using var valuesCommand = connection.CreateCommand();
            valuesCommand.CommandText =
                "SELECT group_concat(display_name || repository_url || account_name, '|') FROM git_profiles;";
            var values = (string?)await valuesCommand.ExecuteScalarAsync();
            Assert.DoesNotContain("github-password", values ?? string.Empty);
        }

        await service.DeleteAsync(gitee.Profile.Id);
        var remaining = Assert.Single(await service.ListAsync());
        Assert.Equal(github.Profile.Id, remaining.Profile.Id);
        Assert.True(remaining.Profile.IsDefault);
        await service.DeleteAsync(github.Profile.Id);
        Assert.False(await secretStore.ExistsAsync(
            $"git-password-{github.Profile.Id:N}"));

        SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData(
        GitAuthenticationMethod.Password,
        "git@github.com:owner/repository.git",
        "密码访问方式")]
    [InlineData(
        GitAuthenticationMethod.Ssh,
        "https://github.com/owner/repository.git",
        "SSH 访问方式")]
    public async Task SaveAsync_RequiresAnAddressMatchingTheAuthenticationMethod(
        GitAuthenticationMethod authenticationMethod,
        string repositoryUrl,
        string expectedMessage)
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var service = new GitProfileService(repository, new InMemorySecretStore());

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync(new SaveGitProfileRequest(
                null,
                "错误配置",
                GitHostingProvider.GitHub,
                repositoryUrl,
                "main",
                authenticationMethod,
                "owner",
                "password",
                authenticationMethod == GitAuthenticationMethod.Ssh
                    ? CreateSshKeyPair(directory.Path)
                    : null,
                IsDefault: false)));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.Empty(await service.ListAsync());
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ListAsync_MigratesTheVersion164AccessTokenCredential()
    {
        using var directory = new TestDirectory();
        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "cdsi.db"));
        await repository.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var profile = new GitProfile(
            Guid.NewGuid(),
            "旧 GitHub",
            GitHostingProvider.GitHub,
            "https://github.com/owner/repository.git",
            "main",
            GitAuthenticationMethod.Password,
            "owner",
            null,
            true,
            now,
            now);
        await repository.SaveGitProfileAsync(profile);
        var secretStore = new InMemorySecretStore();
        await secretStore.StoreAsync(
            $"git-access-token-{profile.Id:N}",
            "legacy-token");
        var service = new GitProfileService(repository, secretStore);

        var configured = Assert.Single(await service.ListAsync());

        Assert.True(configured.HasPassword);
        Assert.Equal("legacy-token", await service.GetPasswordAsync(profile.Id));
        Assert.False(await secretStore.ExistsAsync(
            $"git-access-token-{profile.Id:N}"));
        SqliteConnection.ClearAllPools();
    }

    private static string CreateSshKeyPair(string root)
    {
        var sshDirectory = Path.Combine(root, ".ssh");
        Directory.CreateDirectory(sshDirectory);
        var privateKeyPath = Path.Combine(sshDirectory, "id_ed25519");
        var publicKeyPath = privateKeyPath + ".pub";
        File.WriteAllText(privateKeyPath, "test-private-key-fixture");
        File.WriteAllText(publicKeyPath, "ssh-ed25519 test-public-key");
        return publicKeyPath;
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = [];

        public Task StoreAsync(
            string key,
            string secret,
            CancellationToken cancellationToken = default)
        {
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_secrets.ContainsKey(key));
        }

        public Task<string?> RetrieveAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_secrets.GetValueOrDefault(key));
        }

        public Task DeleteAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            _secrets.Remove(key);
            return Task.CompletedTask;
        }
    }
}
