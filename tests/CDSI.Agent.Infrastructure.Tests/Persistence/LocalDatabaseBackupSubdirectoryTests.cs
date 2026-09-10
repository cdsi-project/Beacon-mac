using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class LocalDatabaseBackupSubdirectoryTests
{
    [Fact]
    public async Task Snapshot_CanUseAnIsolatedReaderSubdirectory()
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, "reader.db");
        await using (var connection = new SqliteConnection(
                         $"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE sample(id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        var service = new LocalDatabaseBackupService(databasePath, "0.2.15", "Reader");
        var result = await service.CreateSnapshotAsync(directory.Path, force: true);

        Assert.True(result.Created);
        Assert.Equal(
            Path.Combine(directory.Path, "System", "DatabaseBackups", "Reader"),
            result.BackupDirectory);
        Assert.True(File.Exists(result.SnapshotPath));
    }

    [Theory]
    [InlineData("../Reader")]
    [InlineData("Reader/Other")]
    public void Constructor_RejectsNestedBackupSubdirectory(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            new LocalDatabaseBackupService("reader.db", "0.2.15", value));
    }
}
