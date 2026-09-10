using CDSI.Agent.Application.OpenWeb;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.OpenWeb;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.OpenWeb;

public sealed class OpenWebSettingsServiceTests
{
    [Fact]
    public async Task SaveAsync_ManagesMultipleSourcesAndExactlyOneDefault()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var secretStore = new InMemorySecretStore();
        var service = new OpenWebSettingsService(repository, secretStore);

        var first = await service.SaveAsync(new SaveOpenWebSourceRequest(
            null,
            "主站",
            "ORIGIN.Example.COM.",
            " editor ",
            "abcd efgh ijkl mnop",
            IsDefault: false));
        var second = await service.SaveAsync(new SaveOpenWebSourceRequest(
            null,
            "作品站",
            "works.example.com",
            "author",
            "second-password",
            IsDefault: true));
        var sources = await service.ListAsync();
        var firstConnection = await service.GetConnectionAsync(first.Source.Id);
        var secondConnection = await service.GetConnectionAsync(second.Source.Id);

        Assert.Equal(2, sources.Count);
        Assert.Equal(second.Source.Id, Assert.Single(
            sources,
            source => source.Source.IsDefault).Source.Id);
        Assert.Equal("origin.example.com", first.Source.OriginDomain);
        Assert.Equal("editor", first.Source.WordPressUsername);
        Assert.Equal("abcdefghijklmnop", firstConnection.ApplicationPassword);
        Assert.Equal("second-password", secondConnection.ApplicationPassword);
        Assert.DoesNotContain(
            firstConnection.ApplicationPassword,
            firstConnection.ToString(),
            StringComparison.Ordinal);

        await service.SetDefaultAsync(first.Source.Id);
        await service.DeleteAsync(first.Source.Id);
        sources = await service.ListAsync();

        Assert.Equal(second.Source.Id, Assert.Single(sources).Source.Id);
        Assert.True(sources[0].Source.IsDefault);

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT group_concat(display_name || origin_domain || wordpress_username, '|') FROM openweb_sources;";
            var storedValues = (string?)await command.ExecuteScalarAsync();
            Assert.DoesNotContain("second-password", storedValues ?? string.Empty);
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ListAsync_MigratesTheLegacySourceAndCredential()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "cdsi.db");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DROP TABLE asset_tag_links;
                DROP TABLE asset_tags;
                DROP TABLE asset_directory_exclusions;
                DROP TABLE asset_collection_deletion_audit;
                DROP TABLE git_profiles;
                ALTER TABLE asset_locations DROP COLUMN excluded_from_asset_list_at;
                ALTER TABLE asset_locations DROP COLUMN excluded_from_asset_list;
                DROP TABLE restore_items;
                DROP TABLE restore_jobs;
                DROP TABLE openweb_sources;
                ALTER TABLE scan_roots DROP COLUMN idle_scan_unit;
                ALTER TABLE scan_roots DROP COLUMN idle_scan_interval;
                ALTER TABLE scan_roots DROP COLUMN idle_scan_enabled;
                ALTER TABLE scan_roots DROP COLUMN file_type_filters_json;
                DELETE FROM schema_migrations WHERE version >= 16;
                INSERT INTO agent_settings(setting_key, setting_value, updated_at)
                VALUES
                    ('openweb.origin_domain', 'legacy.example.com', $updated_at),
                    ('openweb.wordpress_username', 'legacy-editor', $updated_at);
                """;
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await repository.InitializeAsync();
        var secretStore = new InMemorySecretStore();
        await secretStore.StoreAsync("openweb-wordpress", "legacy-password");
        var service = new OpenWebSettingsService(repository, secretStore);

        var configured = Assert.Single(await service.ListAsync());
        var connectionResult = await service.GetConnectionAsync(configured.Source.Id);

        Assert.Equal(OpenWebSource.MigratedLegacySourceId, configured.Source.Id);
        Assert.Equal("legacy.example.com", configured.Source.OriginDomain);
        Assert.True(configured.Source.IsDefault);
        Assert.True(configured.HasApplicationPassword);
        Assert.Equal("legacy-password", connectionResult.ApplicationPassword);
        Assert.False(await secretStore.ExistsAsync("openweb-wordpress"));
        SqliteConnection.ClearAllPools();
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
