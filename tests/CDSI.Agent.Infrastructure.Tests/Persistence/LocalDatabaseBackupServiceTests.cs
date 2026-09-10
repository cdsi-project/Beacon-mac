using System.Text.Json;
using CDSI.Agent.Infrastructure.Persistence;
using CDSI.Agent.Infrastructure.Reader;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class LocalDatabaseBackupServiceTests
{
    [Fact]
    public async Task CreateSnapshotAsync_CreatesVerifiedSnapshotAndManifest()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "State", "cdsi.db");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        await repository.GetOrCreateDeviceIdAsync();
        var service = new LocalDatabaseBackupService(databasePath, "0.test");

        var result = await service.CreateSnapshotAsync(workspacePath);

        Assert.True(result.Created);
        Assert.True(File.Exists(result.SnapshotPath));
        Assert.Equal(
            Path.Combine(workspacePath, "System", "DatabaseBackups"),
            result.BackupDirectory);
        var manifestPath = $"{result.SnapshotPath}.json";
        Assert.True(File.Exists(manifestPath));
        var manifest = JsonSerializer.Deserialize<LocalDatabaseBackupManifest>(
            await File.ReadAllTextAsync(manifestPath));
        Assert.NotNull(manifest);
        Assert.Equal("0.test", manifest.ApplicationVersion);
        Assert.Equal(new FileInfo(result.SnapshotPath).Length, manifest.DatabaseSize);
        Assert.Matches("^[0-9A-F]{64}$", manifest.Sha256);

        await using var backup = new SqliteConnection(
            $"Data Source={result.SnapshotPath};Mode=ReadOnly;Pooling=False");
        await backup.OpenAsync();
        await using var command = backup.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", await command.ExecuteScalarAsync());

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CreateSnapshotAsync_SkipsUnchangedDatabaseAndBacksUpNewChanges()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "State", "cdsi.db");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var service = new LocalDatabaseBackupService(databasePath, "0.test");

        var first = await service.CreateSnapshotAsync(workspacePath);
        var unchanged = await service.CreateSnapshotAsync(workspacePath);
        await repository.GetOrCreateDeviceIdAsync();
        var changed = await service.CreateSnapshotAsync(workspacePath);

        Assert.True(first.Created);
        Assert.False(unchanged.Created);
        Assert.Equal(first.SnapshotPath, unchanged.SnapshotPath);
        Assert.True(changed.Created);
        Assert.NotEqual(first.SnapshotPath, changed.SnapshotPath);
        Assert.Equal(
            2,
            Directory.EnumerateFiles(
                first.BackupDirectory,
                "cdsi-*.db",
                SearchOption.TopDirectoryOnly).Count());

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CreateSnapshotAsync_DoesNotPublishCorruptSnapshot()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "State", "cdsi.db");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await File.WriteAllTextAsync(databasePath, "not a sqlite database");
        var service = new LocalDatabaseBackupService(databasePath, "0.test");

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.CreateSnapshotAsync(workspacePath));

        var backupDirectory = service.GetBackupDirectory(workspacePath);
        Assert.Empty(Directory.EnumerateFiles(backupDirectory, "cdsi-*.db"));
        Assert.Empty(Directory.EnumerateFiles(backupDirectory, "*.tmp*"));

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CreateSnapshotAsync_DoesNotReuseDamagedLatestSnapshot()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "State", "cdsi.db");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var service = new LocalDatabaseBackupService(databasePath, "0.test");
        var first = await service.CreateSnapshotAsync(workspacePath);
        await File.WriteAllTextAsync(first.SnapshotPath, "damaged snapshot");

        var replacement = await service.CreateSnapshotAsync(workspacePath);

        Assert.True(replacement.Created);
        Assert.NotEqual(first.SnapshotPath, replacement.SnapshotPath);
        await using var connection = new SqliteConnection(
            $"Data Source={replacement.SnapshotPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CreateSnapshotAsync_DetectsChangesStoredOnlyInWal()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "State", "reader.db");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        await ReaderDatabaseMigrator.MigrateAsync(
            connectionString,
            CancellationToken.None);
        await using var writer = new SqliteConnection(connectionString);
        await writer.OpenAsync();
        await using (var setup = writer.CreateCommand())
        {
            setup.CommandText =
                """
                PRAGMA wal_autocheckpoint = 0;
                CREATE TABLE backup_wal_test(value TEXT NOT NULL);
                INSERT INTO backup_wal_test(value) VALUES ('first');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var service = new LocalDatabaseBackupService(
            databasePath,
            "0.test",
            "Reader");
        var first = await service.CreateSnapshotAsync(workspacePath);
        var sourceLength = new FileInfo(databasePath).Length;
        var sourceWriteTime = File.GetLastWriteTimeUtc(databasePath);
        await using (var update = writer.CreateCommand())
        {
            update.CommandText =
                "INSERT INTO backup_wal_test(value) VALUES ('second');";
            await update.ExecuteNonQueryAsync();
        }

        Assert.Equal(sourceLength, new FileInfo(databasePath).Length);
        Assert.Equal(sourceWriteTime, File.GetLastWriteTimeUtc(databasePath));
        var changed = await service.CreateSnapshotAsync(workspacePath);

        Assert.True(first.Created);
        Assert.True(changed.Created);
        Assert.NotEqual(first.SnapshotPath, changed.SnapshotPath);
        await using var snapshot = new SqliteConnection(
            $"Data Source={changed.SnapshotPath};Mode=ReadOnly;Pooling=False");
        await snapshot.OpenAsync();
        await using var count = snapshot.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM backup_wal_test;";
        Assert.Equal(2L, await count.ExecuteScalarAsync());
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task CreateSnapshotAsync_PrunesExcessRecentSnapshots()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "State", "cdsi.db");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        var repository = new SqliteAssetRepository(databasePath);
        await repository.InitializeAsync();
        var service = new LocalDatabaseBackupService(databasePath, "0.test");

        for (var index = 0; index < 27; index++)
        {
            await service.CreateSnapshotAsync(workspacePath, force: true);
        }

        var backupDirectory = service.GetBackupDirectory(workspacePath);
        Assert.Equal(
            24,
            Directory.EnumerateFiles(backupDirectory, "cdsi-*.db").Count());
        Assert.Equal(
            24,
            Directory.EnumerateFiles(backupDirectory, "cdsi-*.db.json").Count());

        SqliteConnection.ClearAllPools();
    }
}
