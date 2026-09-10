using System.IO.Compression;
using System.Text.Json;
using CDSI.Agent.Infrastructure.Persistence;
using CDSI.Agent.Infrastructure.Reader;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Tests.Persistence;

public sealed class LocalStateProtectionServiceTests
{
    private const string ApplicationVersion = "0.test";

    [Fact]
    public async Task CreateBackupAsync_CreatesExactlyThreeValidatedEntries()
    {
        using var directory = new TestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "Data");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        var assetDatabasePath = Path.Combine(dataDirectory, "cdsi.db");
        var readerDatabasePath = Path.Combine(dataDirectory, "reader.db");
        await CreateStateDatabasesAsync(
            assetDatabasePath,
            readerDatabasePath,
            "original");
        var service = new LocalStateProtectionService(
            dataDirectory,
            assetDatabasePath,
            readerDatabasePath,
            ApplicationVersion);

        var backup = await service.CreateBackupAsync(
            workspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D"));

        Assert.Equal(LocalStateBackupStatus.Restorable, backup.Status);
        Assert.Equal(LocalStateBackupKind.Manual, backup.Kind);
        Assert.Equal(64, backup.BundleSha256?.Length);
        Assert.Equal(
            await StateBundleArchive.ComputeSha256Async(
                backup.Path,
                CancellationToken.None),
            backup.BundleSha256);
        Assert.EndsWith(".cdsibak", backup.Path, StringComparison.OrdinalIgnoreCase);
        using (var archive = ZipFile.OpenRead(backup.Path))
        {
            Assert.Equal(
                [
                    StateBundleArchive.ManifestEntryName,
                    StateBundleArchive.AssetDatabaseEntryName,
                    StateBundleArchive.ReaderDatabaseEntryName
                ],
                archive.Entries.Select(entry => entry.FullName).ToArray());
        }

        var inspected = await service.InspectAsync(backup.Path);
        Assert.Equal(LocalStateBackupStatus.Restorable, inspected.Status);
        Assert.Equal(backup.BackupId, inspected.BackupId);
        Assert.Equal(backup.BundleSha256, inspected.BundleSha256);
        Assert.Null(inspected.Error);
    }

    [Fact]
    public async Task InspectAsync_RejectsUndefinedNumericBackupKind()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "original");
        var backup = await live.Protection.CreateBackupAsync(
            live.WorkspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D"));

        using (var archive = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        {
            var manifestEntry = archive.GetEntry(StateBundleArchive.ManifestEntryName)!;
            StateBundleManifest manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = (await JsonSerializer.DeserializeAsync<StateBundleManifest>(
                    stream,
                    StateBundleArchive.JsonOptions))!;
            }

            manifestEntry.Delete();
            var replacement = archive.CreateEntry(StateBundleArchive.ManifestEntryName);
            await using var output = replacement.Open();
            await JsonSerializer.SerializeAsync(
                output,
                manifest with { BackupKind = "99" },
                StateBundleArchive.JsonOptions);
        }

        var inspected = await live.Protection.InspectAsync(backup.Path);

        Assert.Equal(LocalStateBackupStatus.Invalid, inspected.Status);
    }

    [Fact]
    public async Task ListBackupsAsync_RemovesOnlyStaleOwnedTemporaryBundles()
    {
        using var directory = new TestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "Data");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        var service = new LocalStateProtectionService(
            dataDirectory,
            Path.Combine(dataDirectory, "cdsi.db"),
            Path.Combine(dataDirectory, "reader.db"),
            ApplicationVersion);
        var backupDirectory = service.GetBackupDirectory(workspacePath);
        Directory.CreateDirectory(backupDirectory);
        var destinationName =
            $"beacon-state-20260904-010203-004Z-{Guid.NewGuid():N}.cdsibak";
        var staleOwned = Path.Combine(
            backupDirectory,
            $".{destinationName}.{Guid.NewGuid():N}.tmp");
        var recentOwned = Path.Combine(
            backupDirectory,
            $".{destinationName}.{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Combine(backupDirectory, ".unrelated.cdsibak.bad.tmp");
        await File.WriteAllTextAsync(staleOwned, "sensitive");
        await File.WriteAllTextAsync(recentOwned, "active");
        await File.WriteAllTextAsync(unrelated, "keep");
        File.SetLastWriteTimeUtc(staleOwned, DateTime.UtcNow.AddHours(-2));

        Assert.Empty(await service.ListBackupsAsync(workspacePath));

        Assert.False(File.Exists(staleOwned));
        Assert.True(File.Exists(recentOwned));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void ValidateWorkspaceBackupPath_PreservesDriveRootContainment()
    {
        using var directory = new TestDirectory();
        var root = Path.GetPathRoot(directory.Path)!;

        LocalStateProtectionService.ValidateWorkspaceBackupPath(
            root,
            Path.Combine(directory.Path, "System", "StateBackups"));
    }

    [Fact]
    public async Task ListBackupsAsync_RejectsReparsePointInWorkspaceAncestorBeforeCleanup()
    {
        using var directory = new TestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "Data");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        var outsideSystem = Path.Combine(directory.Path, "OutsideSystem");
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(outsideSystem);
        try
        {
            Directory.CreateSymbolicLink(
                Path.Combine(workspacePath, "System"),
                outsideSystem);
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException or
                                          PlatformNotSupportedException)
        {
            return;
        }

        var outsideBackups = Path.Combine(outsideSystem, "StateBackups");
        Directory.CreateDirectory(outsideBackups);
        var destinationName =
            $"beacon-state-20260904-010203-004Z-{Guid.NewGuid():N}.cdsibak";
        var staleOutside = Path.Combine(
            outsideBackups,
            $".{destinationName}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(staleOutside, "keep");
        File.SetLastWriteTimeUtc(staleOutside, DateTime.UtcNow.AddHours(-2));
        var service = new LocalStateProtectionService(
            dataDirectory,
            Path.Combine(dataDirectory, "cdsi.db"),
            Path.Combine(dataDirectory, "reader.db"),
            ApplicationVersion);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ListBackupsAsync(workspacePath));

        Assert.True(File.Exists(staleOutside));
    }

    [Fact]
    public async Task InspectAsync_RejectsDatabaseWhoseChecksumNoLongerMatches()
    {
        using var directory = new TestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "Data");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        var assetDatabasePath = Path.Combine(dataDirectory, "cdsi.db");
        var readerDatabasePath = Path.Combine(dataDirectory, "reader.db");
        await CreateStateDatabasesAsync(
            assetDatabasePath,
            readerDatabasePath,
            "original");
        var service = new LocalStateProtectionService(
            dataDirectory,
            assetDatabasePath,
            readerDatabasePath,
            ApplicationVersion);
        var backup = await service.CreateBackupAsync(
            workspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D"));

        using (var archive = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(StateBundleArchive.ReaderDatabaseEntryName)!;
            await using var stream = entry.Open();
            stream.Position = 0;
            stream.WriteByte(0x7f);
        }

        var inspected = await service.InspectAsync(backup.Path);

        Assert.Equal(LocalStateBackupStatus.Invalid, inspected.Status);
        Assert.NotNull(inspected.Error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    public async Task InspectAsync_RejectsUnsafeOrIncompleteArchiveStructure(
        string mutation)
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "original");
        var backup = await live.Protection.CreateBackupAsync(
            live.WorkspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D"));

        using (var archive = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        {
            if (string.Equals(mutation, "missing", StringComparison.Ordinal))
            {
                archive.GetEntry(StateBundleArchive.ReaderDatabaseEntryName)!.Delete();
            }
            else if (string.Equals(mutation, "traversal", StringComparison.Ordinal))
            {
                var readerEntry = archive.GetEntry(
                    StateBundleArchive.ReaderDatabaseEntryName)!;
                byte[] content;
                await using (var stream = readerEntry.Open())
                await using (var buffer = new MemoryStream())
                {
                    await stream.CopyToAsync(buffer);
                    content = buffer.ToArray();
                }

                readerEntry.Delete();
                var unsafeEntry = archive.CreateEntry("../reader.db");
                await using var output = unsafeEntry.Open();
                await output.WriteAsync(content);
            }
            else if (string.Equals(mutation, "duplicate", StringComparison.Ordinal))
            {
                archive.CreateEntry(StateBundleArchive.ManifestEntryName);
            }
            else
            {
                archive.CreateEntry("extra.txt");
            }
        }

        var inspected = await live.Protection.InspectAsync(backup.Path);

        Assert.Equal(LocalStateBackupStatus.Invalid, inspected.Status);
    }

    [Fact]
    public async Task InspectAsync_RejectsNullDatabaseManifestEntries()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "original");
        var backup = await live.Protection.CreateBackupAsync(
            live.WorkspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D"));

        using (var archive = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        {
            var manifestEntry = archive.GetEntry(StateBundleArchive.ManifestEntryName)!;
            StateBundleManifest manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = (await JsonSerializer.DeserializeAsync<StateBundleManifest>(
                    stream,
                    StateBundleArchive.JsonOptions))!;
            }

            manifestEntry.Delete();
            var replacement = archive.CreateEntry(StateBundleArchive.ManifestEntryName);
            await using var output = replacement.Open();
            await JsonSerializer.SerializeAsync(
                output,
                manifest with
                {
                    Databases = new StateBundleDatabaseManifest[] { null!, null! }
                },
                StateBundleArchive.JsonOptions);
        }

        var inspected = await live.Protection.InspectAsync(backup.Path);

        Assert.Equal(LocalStateBackupStatus.Invalid, inspected.Status);
    }

    [Fact]
    public async Task InspectAsync_MissingFileReturnsInvalidResult()
    {
        using var directory = new TestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "Data");
        var service = new LocalStateProtectionService(
            dataDirectory,
            Path.Combine(dataDirectory, "cdsi.db"),
            Path.Combine(dataDirectory, "reader.db"),
            ApplicationVersion);

        var inspected = await service.InspectAsync(
            Path.Combine(directory.Path, "missing.cdsibak"));

        Assert.Equal(LocalStateBackupStatus.Invalid, inspected.Status);
        Assert.Equal(0, inspected.FileSize);
    }

    [Theory]
    [InlineData("asset", false)]
    [InlineData("asset", true)]
    [InlineData("reader", false)]
    [InlineData("reader", true)]
    public async Task CreateBackupAsync_DoesNotPublishWhenSourceDatabaseIsUnavailable(
        string role,
        bool damaged)
    {
        using var directory = new TestDirectory();
        var dataDirectory = Path.Combine(directory.Path, "Data");
        var workspacePath = Path.Combine(directory.Path, "Workspace");
        var assetDatabasePath = Path.Combine(dataDirectory, "cdsi.db");
        var readerDatabasePath = Path.Combine(dataDirectory, "reader.db");
        await CreateStateDatabasesAsync(
            assetDatabasePath,
            readerDatabasePath,
            "original");
        var unavailablePath = string.Equals(role, "asset", StringComparison.Ordinal)
            ? assetDatabasePath
            : readerDatabasePath;
        if (damaged)
        {
            await File.WriteAllBytesAsync(
                unavailablePath,
                [0x43, 0x44, 0x53, 0x49]);
        }
        else
        {
            File.Delete(unavailablePath);
        }

        var service = new LocalStateProtectionService(
            dataDirectory,
            assetDatabasePath,
            readerDatabasePath,
            ApplicationVersion);

        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateBackupAsync(
            workspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D")));

        var backupDirectory = service.GetBackupDirectory(workspacePath);
        if (Directory.Exists(backupDirectory))
        {
            Assert.Empty(Directory.EnumerateFiles(
                backupDirectory,
                "*.cdsibak",
                SearchOption.TopDirectoryOnly));
        }
    }

    [Fact]
    public async Task InspectAsync_RejectsManifestWithNewerSchemaVersion()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "original");
        var backup = await live.Protection.CreateBackupAsync(
            live.WorkspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D"));

        using (var archive = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        {
            var manifestEntry = archive.GetEntry(StateBundleArchive.ManifestEntryName)!;
            StateBundleManifest manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = (await JsonSerializer.DeserializeAsync<StateBundleManifest>(
                    stream,
                    StateBundleArchive.JsonOptions))!;
            }

            manifestEntry.Delete();
            var changedManifest = manifest with
            {
                Databases = manifest.Databases
                    .Select(database => string.Equals(
                            database.Role,
                            "asset",
                            StringComparison.Ordinal)
                        ? database with
                        {
                            SchemaVersion = DatabaseMigrator.CurrentSchemaVersion + 1
                        }
                        : database)
                    .ToArray()
            };
            var replacement = archive.CreateEntry(
                StateBundleArchive.ManifestEntryName,
                CompressionLevel.Optimal);
            await using var output = replacement.Open();
            await JsonSerializer.SerializeAsync(
                output,
                changedManifest,
                StateBundleArchive.JsonOptions);
        }

        var inspected = await live.Protection.InspectAsync(backup.Path);

        Assert.Equal(LocalStateBackupStatus.NewerVersion, inspected.Status);
    }

    [Fact]
    public async Task InspectAsync_RejectsNewerDatabaseWhenManifestClaimsCurrentSchema()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "original");
        var backup = await live.Protection.CreateBackupAsync(
            live.WorkspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D"));
        var changedDatabasePath = Path.Combine(directory.Path, "newer-cdsi.db");

        using (var archive = ZipFile.Open(backup.Path, ZipArchiveMode.Update))
        {
            var databaseEntry = archive.GetEntry(
                StateBundleArchive.AssetDatabaseEntryName)!;
            await using (var source = databaseEntry.Open())
            await using (var destination = new FileStream(
                             changedDatabasePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await source.CopyToAsync(destination);
            }

            await using (var connection = new SqliteConnection(
                             CreateConnectionString(
                                 changedDatabasePath,
                                 SqliteOpenMode.ReadWrite)))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO schema_migrations(version, applied_at)
                    VALUES ($version, $applied_at);
                    """;
                command.Parameters.AddWithValue(
                    "$version",
                    DatabaseMigrator.CurrentSchemaVersion + 1);
                command.Parameters.AddWithValue(
                    "$applied_at",
                    DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            var manifestEntry = archive.GetEntry(StateBundleArchive.ManifestEntryName)!;
            StateBundleManifest manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = (await JsonSerializer.DeserializeAsync<StateBundleManifest>(
                    stream,
                    StateBundleArchive.JsonOptions))!;
            }

            var changedSize = new FileInfo(changedDatabasePath).Length;
            var changedSha256 = await StateBundleArchive.ComputeSha256Async(
                changedDatabasePath,
                CancellationToken.None);
            var changedManifest = manifest with
            {
                Databases = manifest.Databases
                    .Select(database => string.Equals(
                            database.Role,
                            "asset",
                            StringComparison.Ordinal)
                        ? database with
                        {
                            Size = changedSize,
                            Sha256 = changedSha256
                        }
                        : database)
                    .ToArray()
            };

            databaseEntry.Delete();
            var replacementDatabase = archive.CreateEntry(
                StateBundleArchive.AssetDatabaseEntryName,
                CompressionLevel.Optimal);
            await using (var source = File.OpenRead(changedDatabasePath))
            await using (var destination = replacementDatabase.Open())
            {
                await source.CopyToAsync(destination);
            }

            manifestEntry.Delete();
            var replacementManifest = archive.CreateEntry(
                StateBundleArchive.ManifestEntryName,
                CompressionLevel.Optimal);
            await using var output = replacementManifest.Open();
            await JsonSerializer.SerializeAsync(
                output,
                changedManifest,
                StateBundleArchive.JsonOptions);
        }

        var inspected = await live.Protection.InspectAsync(backup.Path);

        Assert.Equal(LocalStateBackupStatus.NewerVersion, inspected.Status);
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_RejectsMissingCurrentSchemaTable()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await ExecuteSqlAsync(
            live.AssetDatabasePath,
            "DROP TABLE managed_workspaces;");

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.AssetDatabasePath,
                "asset",
                DatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_RejectsMissingRepositoryCriticalColumn()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await ExecuteSqlAsync(
            live.AssetDatabasePath,
            """
            DROP INDEX ix_assets_mime_type;
            ALTER TABLE assets DROP COLUMN mime_type;
            """);

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.AssetDatabasePath,
                "asset",
                DatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Fact]
    public async Task CopyBoundedAsync_StopsBeforeWritingPastLimit()
    {
        await using var source = new MemoryStream(new byte[17]);
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.CopyBoundedAsync(
                source,
                destination,
                expectedLength: 8,
                maximumLength: 16,
                CancellationToken.None));

        Assert.True(destination.Length <= 8);
    }

    [Fact]
    public async Task EnsureStagedBundleLengthWithinLimit_RejectsBeforeHashingOversizedFile()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "restore.cdsibak");
        await File.WriteAllBytesAsync(path, [1, 2]);

        Assert.Throws<StateBackupValidationException>(() =>
            PendingStateRestoreService.EnsureStagedBundleLengthWithinLimit(
                path,
                maximumArchiveBytes: 1));
    }

    [Fact]
    public async Task EnsureArchiveLengthWithinLimit_RejectsOversizedCompletedArchive()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "completed.cdsibak");
        await File.WriteAllBytesAsync(path, [1, 2]);

        Assert.Throws<StateBackupValidationException>(() =>
            StateBundleArchive.EnsureArchiveLengthWithinLimit(
                path,
                maximumBytes: 1));
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_RejectsMissingReaderSchemaTable()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await ExecuteSqlAsync(
            live.ReaderDatabasePath,
            "DROP TABLE reader_entry_states;");

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.ReaderDatabasePath,
                "reader",
                ReaderDatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_RejectsMigrationHistoryGap()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await ExecuteSqlAsync(
            live.AssetDatabasePath,
            "DELETE FROM schema_migrations WHERE version = 14;");

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.AssetDatabasePath,
                "asset",
                DatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_ClassifiesInt64NewerSchemaWithoutOverflow()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await ExecuteSqlAsync(
            live.AssetDatabasePath,
            """
            INSERT INTO schema_migrations(version, applied_at)
            VALUES (2147483648, 'newer');
            """);

        await Assert.ThrowsAsync<StateBackupNewerVersionException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.AssetDatabasePath,
                "asset",
                expectedSchemaVersion: null,
                CancellationToken.None));
    }

    [Fact]
    public async Task ValidateSqliteDatabaseAsync_RejectsWrongSchemaVersionType()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await ExecuteSqlAsync(
            live.AssetDatabasePath,
            """
            ALTER TABLE schema_migrations RENAME TO original_schema_migrations;
            CREATE TABLE schema_migrations (
                version TEXT NOT NULL,
                applied_at TEXT NOT NULL);
            INSERT INTO schema_migrations(version, applied_at)
            VALUES ('invalid', 'invalid');
            DROP TABLE original_schema_migrations;
            """);

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.AssetDatabasePath,
                "asset",
                expectedSchemaVersion: null,
                CancellationToken.None));
    }

    [Theory]
    [InlineData("primary-key")]
    [InlineData("unique-key")]
    [InlineData("foreign-key")]
    public async Task ValidateSqliteDatabaseAsync_RejectsMissingRuntimeConstraint(
        string mutation)
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var sql = mutation switch
        {
            "primary-key" =>
                """
                DROP TABLE reader_fetch_logs;
                CREATE TABLE reader_fetch_logs (
                    id TEXT NOT NULL,
                    feed_id TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NOT NULL,
                    http_status INTEGER NULL,
                    result TEXT NOT NULL,
                    new_entries INTEGER NOT NULL,
                    response_bytes INTEGER NOT NULL,
                    duration_ms INTEGER NOT NULL,
                    error TEXT NULL,
                    FOREIGN KEY (feed_id) REFERENCES reader_feeds(id) ON DELETE CASCADE);
                """,
            "unique-key" =>
                """
                PRAGMA foreign_keys=OFF;
                DROP TABLE reader_entries;
                CREATE TABLE reader_entries (
                    id TEXT NOT NULL PRIMARY KEY,
                    feed_id TEXT NOT NULL,
                    dedupe_key TEXT NOT NULL,
                    external_id TEXT NULL,
                    title TEXT NOT NULL,
                    url TEXT NULL,
                    author TEXT NULL,
                    summary TEXT NULL,
                    content TEXT NULL,
                    published_at TEXT NULL,
                    updated_at TEXT NULL,
                    fetched_at TEXT NOT NULL,
                    FOREIGN KEY (feed_id) REFERENCES reader_feeds(id) ON DELETE CASCADE);
                PRAGMA foreign_keys=ON;
                """,
            _ =>
                """
                DROP TABLE reader_fetch_logs;
                CREATE TABLE reader_fetch_logs (
                    id TEXT NOT NULL PRIMARY KEY,
                    feed_id TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NOT NULL,
                    http_status INTEGER NULL,
                    result TEXT NOT NULL,
                    new_entries INTEGER NOT NULL,
                    response_bytes INTEGER NOT NULL,
                    duration_ms INTEGER NOT NULL,
                    error TEXT NULL);
                """
        };
        await ExecuteSqlAsync(live.ReaderDatabasePath, sql);

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.ReaderDatabasePath,
                "reader",
                ReaderDatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(
        "reader",
        "table",
        "reader_fetch_logs",
        "response_bytes INTEGER NOT NULL",
        "response_bytes TEXT NOT NULL")]
    [InlineData(
        "reader",
        "table",
        "reader_entry_states",
        "is_read INTEGER NOT NULL DEFAULT 0",
        "is_read INTEGER NOT NULL DEFAULT 1")]
    [InlineData(
        "reader",
        "table",
        "reader_fetch_logs",
        "REFERENCES reader_feeds(id) ON DELETE CASCADE",
        "REFERENCES reader_feeds(id) ON UPDATE CASCADE ON DELETE CASCADE")]
    [InlineData(
        "asset",
        "index",
        "ux_openweb_sources_default",
        "WHERE is_default = 1",
        "WHERE is_default = 0")]
    [InlineData(
        "asset",
        "table",
        "asset_collections",
        "name TEXT NOT NULL COLLATE NOCASE UNIQUE",
        "name TEXT NOT NULL UNIQUE")]
    [InlineData(
        "reader",
        "table",
        "reader_feeds",
        "CHECK(is_enabled IN (0, 1))",
        "CHECK(is_enabled IN (0, 1, 2))")]
    [InlineData(
        "reader",
        "table",
        "reader_feeds",
        "CHECK(is_enabled IN (0, 1))",
        "CHECK(is_enabled IN (0, 1, 2)) /* CHECK(is_enabled IN (0, 1)) */")]
    [InlineData(
        "reader",
        "table",
        "reader_fetch_logs",
        "result TEXT NOT NULL",
        "result TEXT NOT NULL COLLATE NOCASE")]
    [InlineData(
        "reader",
        "table",
        "reader_feeds",
        "feed_url_key TEXT NOT NULL UNIQUE",
        "feed_url_key TEXT NOT NULL UNIQUE ON CONFLICT REPLACE")]
    public async Task ValidateSqliteDatabaseAsync_RejectsMalformedRuntimeSchemaShape(
        string role,
        string schemaObjectType,
        string schemaObjectName,
        string oldSql,
        string newSql)
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var databasePath = string.Equals(role, "asset", StringComparison.Ordinal)
            ? live.AssetDatabasePath
            : live.ReaderDatabasePath;
        await RewriteSchemaSqlAsync(
            databasePath,
            schemaObjectType,
            schemaObjectName,
            oldSql,
            newSql);

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                databasePath,
                role,
                string.Equals(role, "asset", StringComparison.Ordinal)
                    ? DatabaseMigrator.CurrentSchemaVersion
                    : ReaderDatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(
        "ALTER TABLE reader_fetch_logs " +
        "ADD COLUMN unsupported_value TEXT NOT NULL DEFAULT 'x';")]
    [InlineData(
        "CREATE UNIQUE INDEX ux_unsupported_fetch_result " +
        "ON reader_fetch_logs(result);")]
    [InlineData(
        "CREATE VIEW unsupported_reader_view AS " +
        "SELECT id FROM reader_feeds;")]
    [InlineData(
        "CREATE TRIGGER unsupported_reader_trigger " +
        "AFTER INSERT ON reader_fetch_logs BEGIN SELECT 1; END;")]
    [InlineData(
        "CREATE TABLE unsupported_reader_table (" +
        "feed_id TEXT NOT NULL REFERENCES reader_feeds(id) ON DELETE RESTRICT);")]
    [InlineData(
        "CREATE VIRTUAL TABLE unsupported_reader_search USING fts5(content);")]
    public async Task ValidateSqliteDatabaseAsync_RejectsUnsupportedSchemaAdditions(
        string sql)
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await ExecuteSqlAsync(live.ReaderDatabasePath, sql);

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.ReaderDatabasePath,
                "reader",
                ReaderDatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(
        "CREATE INDEX poison_expression ON " +
        "assets(json_extract(original_filename, '$.x'));")]
    [InlineData(
        "CREATE INDEX poison_partial ON assets(original_filename) " +
        "WHERE json_extract(original_filename, '$.x') IS NOT NULL;")]
    [InlineData("DROP INDEX ix_assets_discovered_at;")]
    public async Task ValidateSqliteDatabaseAsync_RejectsNonCanonicalIndexes(
        string sql)
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await ExecuteSqlAsync(live.AssetDatabasePath, sql);

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.AssetDatabasePath,
                "asset",
                DatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(
        "error TEXT NULL,",
        "error TEXT NULL REFERENCES reader_feeds(id),")]
    [InlineData(
        "error TEXT NULL,",
        "error TEXT NULL CHECK(length(error) < 10),")]
    public async Task ValidateSqliteDatabaseAsync_RejectsExtraRuntimeConstraints(
        string oldSql,
        string newSql)
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await RewriteSchemaSqlAsync(
            live.ReaderDatabasePath,
            "table",
            "reader_fetch_logs",
            oldSql,
            newSql);

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.ReaderDatabasePath,
                "reader",
                ReaderDatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Theory]
    [InlineData("VIRTUAL")]
    [InlineData("STORED")]
    public async Task ValidateSqliteDatabaseAsync_RejectsGeneratedColumns(
        string storageKind)
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await RewriteSchemaSqlAsync(
            live.ReaderDatabasePath,
            "table",
            "reader_fetch_logs",
            "error TEXT NULL,",
            "error TEXT NULL, poison TEXT GENERATED ALWAYS AS (NULL) " +
            $"{storageKind} NOT NULL,");

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.ReaderDatabasePath,
                "reader",
                ReaderDatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(" STRICT")]
    [InlineData(" WITHOUT ROWID")]
    public async Task ValidateSqliteDatabaseAsync_RejectsNonCanonicalTableOptions(
        string tableOption)
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await ExecuteSqlAsync(
            live.ReaderDatabasePath,
            """
            DROP TABLE reader_fetch_logs;
            CREATE TABLE reader_fetch_logs (
                id TEXT NOT NULL PRIMARY KEY,
                feed_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT NOT NULL,
                http_status INTEGER NULL,
                result TEXT NOT NULL,
                new_entries INTEGER NOT NULL,
                response_bytes INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                error TEXT NULL,
                FOREIGN KEY (feed_id) REFERENCES reader_feeds(id) ON DELETE CASCADE
            )
            """ + tableOption +
            """
            ;
            CREATE INDEX ix_reader_fetch_logs_feed_time
            ON reader_fetch_logs(feed_id, started_at DESC);
            """);

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            StateBundleArchive.ValidateSqliteDatabaseAsync(
                live.ReaderDatabasePath,
                "reader",
                ReaderDatabaseMigrator.CurrentSchemaVersion,
                CancellationToken.None));
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("reader")]
    public async Task DatabaseMigrators_RejectNewerSchemaBeforeWriting(string role)
    {
        using var directory = new TestDirectory();
        var databasePath = Path.Combine(directory.Path, $"{role}.db");
        var connectionString = CreateConnectionString(
            databasePath,
            SqliteOpenMode.ReadWriteCreate);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = string.Equals(role, "asset", StringComparison.Ordinal)
                ?
                """
                CREATE TABLE schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at TEXT NOT NULL);
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (29, 'newer');
                """
                :
                """
                CREATE TABLE reader_schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at TEXT NOT NULL);
                INSERT INTO reader_schema_migrations(version, applied_at)
                VALUES (2, 'newer');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            string.Equals(role, "asset", StringComparison.Ordinal)
                ? DatabaseMigrator.MigrateAsync(connectionString, CancellationToken.None)
                : ReaderDatabaseMigrator.MigrateAsync(
                    connectionString,
                    CancellationToken.None));

        Assert.Contains("高于", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyPendingAsync_MigratesVersion27AssetDatabaseBeforeReplacement()
    {
        using var directory = new TestDirectory();
        var source = await CreateLiveFixtureAsync(
            directory.Path,
            "Source",
            "version-27");
        await ExecuteSqlAsync(
            source.AssetDatabasePath,
            """
            ALTER TABLE scan_roots DROP COLUMN idle_scan_unit;
            ALTER TABLE scan_roots DROP COLUMN idle_scan_interval;
            ALTER TABLE scan_roots DROP COLUMN idle_scan_enabled;
            DELETE FROM schema_migrations WHERE version = 28;
            """);
        var backup = await source.Protection.CreateBackupAsync(
            source.WorkspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D"));
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        _ = await live.Protection.PrepareRestoreAsync(
            backup.Path,
            live.WorkspacePath,
            Guid.NewGuid().ToString("D"));

        var result = await new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath).ApplyPendingAsync();

        Assert.NotNull(result);
        Assert.Equal("version-27", await ReadMarkerAsync(live.AssetDatabasePath));
        await StateBundleArchive.ValidateSqliteDatabaseAsync(
            live.AssetDatabasePath,
            "asset",
            DatabaseMigrator.CurrentSchemaVersion,
            CancellationToken.None);
    }

    [Fact]
    public async Task ApplyPendingAsync_ReplacesBothDatabasesAndKeepsSafetyBackup()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");

        var preparation = await live.Protection.PrepareRestoreAsync(
            source.BackupPath,
            live.WorkspacePath,
            Guid.NewGuid().ToString("D"));
        var pending = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath);

        var result = await pending.ApplyPendingAsync();

        Assert.NotNull(result);
        Assert.Equal(source.BackupId, result.BackupId);
        Assert.Equal(preparation.SafetyBackupPath, result.SafetyBackupPath);
        Assert.True(File.Exists(result.SafetyBackupPath));
        Assert.False(pending.HasPendingRestore);
        Assert.Equal("backup", await ReadMarkerAsync(live.AssetDatabasePath));
        Assert.Equal("backup", await ReadMarkerAsync(live.ReaderDatabasePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareRestoreAsync_RejectsBundleReplacedAfterUserConfirmation(
        bool emergency)
    {
        using var directory = new TestDirectory();
        var first = await CreateBackupFixtureAsync(
            directory.Path,
            "First",
            "first");
        var second = await CreateBackupFixtureAsync(
            directory.Path,
            "Second",
            "second");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var confirmed = await live.Protection.InspectAsync(first.BackupPath);
        Assert.Equal(LocalStateBackupStatus.Restorable, confirmed.Status);
        File.Copy(second.BackupPath, first.BackupPath, overwrite: true);

        Task<StateRestorePreparation> PrepareAsync() => emergency
            ? live.Protection.PrepareEmergencyRestoreAsync(
                first.BackupPath,
                Guid.NewGuid().ToString("D"),
                confirmed)
            : live.Protection.PrepareRestoreAsync(
                first.BackupPath,
                live.WorkspacePath,
                Guid.NewGuid().ToString("D"),
                confirmed);

        await Assert.ThrowsAsync<StateBackupValidationException>(PrepareAsync);

        var pending = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath);
        Assert.False(pending.HasPendingRestore);
        Assert.Equal("current", await ReadMarkerAsync(live.AssetDatabasePath));
        Assert.Equal("current", await ReadMarkerAsync(live.ReaderDatabasePath));
    }

    [Fact]
    public async Task ExportAsync_RejectsBundleReplacedAfterUserSelection()
    {
        using var directory = new TestDirectory();
        var first = await CreateBackupFixtureAsync(
            directory.Path,
            "First",
            "first");
        var second = await CreateBackupFixtureAsync(
            directory.Path,
            "Second",
            "second");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var confirmed = await live.Protection.InspectAsync(first.BackupPath);
        File.Copy(second.BackupPath, first.BackupPath, overwrite: true);
        var destination = Path.Combine(directory.Path, "export.cdsibak");

        await Assert.ThrowsAsync<StateBackupValidationException>(() =>
            live.Protection.ExportAsync(
                first.BackupPath,
                destination,
                confirmed));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            ".export.cdsibak.*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyPendingAsync_EmergencyRestoreWorksWhenCurrentDatabasesAreMissingOrDamaged(
        bool damaged)
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var fixtureRoot = Path.Combine(directory.Path, "Emergency");
        var dataDirectory = Path.Combine(fixtureRoot, "Data");
        var assetDatabasePath = Path.Combine(dataDirectory, "cdsi.db");
        var readerDatabasePath = Path.Combine(dataDirectory, "reader.db");
        Directory.CreateDirectory(dataDirectory);
        var damagedAsset = new byte[] { 0x43, 0x44, 0x53, 0x49, 0x01 };
        var damagedReader = new byte[] { 0x52, 0x53, 0x53, 0x01 };
        if (damaged)
        {
            await File.WriteAllBytesAsync(assetDatabasePath, damagedAsset);
            await File.WriteAllBytesAsync(readerDatabasePath, damagedReader);
        }

        var protection = new LocalStateProtectionService(
            dataDirectory,
            assetDatabasePath,
            readerDatabasePath,
            ApplicationVersion);
        var preparation = await protection.PrepareEmergencyRestoreAsync(
            source.BackupPath,
            Guid.NewGuid().ToString("D"));

        var result = await new PendingStateRestoreService(
            dataDirectory,
            assetDatabasePath,
            readerDatabasePath).ApplyPendingAsync();

        Assert.NotNull(result);
        Assert.Equal(preparation.SafetyBackupPath, result.SafetyBackupPath);
        Assert.True(Directory.Exists(preparation.SafetyBackupPath));
        Assert.Equal("backup", await ReadMarkerAsync(assetDatabasePath));
        Assert.Equal("backup", await ReadMarkerAsync(readerDatabasePath));
        var manifest = JsonSerializer.Deserialize<RawStateSafetyManifest>(
            await File.ReadAllTextAsync(
                Path.Combine(preparation.SafetyBackupPath, "raw-safety.json")),
            StateBundleArchive.JsonOptions)!;
        Assert.Equal(8, manifest.Files.Count);
        Assert.Equal(
            damaged,
            manifest.Files.Single(file => file.LogicalName == "asset").Existed);
        Assert.Equal(
            damaged,
            manifest.Files.Single(file => file.LogicalName == "reader").Existed);
        if (damaged)
        {
            Assert.Equal(
                damagedAsset,
                await File.ReadAllBytesAsync(
                    Path.Combine(preparation.SafetyBackupPath, "cdsi.db")));
            Assert.Equal(
                damagedReader,
                await File.ReadAllBytesAsync(
                    Path.Combine(preparation.SafetyBackupPath, "reader.db")));
        }
    }

    [Fact]
    public async Task ApplyPendingAsync_EmergencyCaptureRejectsDatabasePathOccupiedByDirectory()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var preparation = await live.Protection.PrepareEmergencyRestoreAsync(
            source.BackupPath,
            Guid.NewGuid().ToString("D"));
        File.Delete(live.AssetDatabasePath);
        Directory.CreateDirectory(live.AssetDatabasePath);

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(() =>
            new PendingStateRestoreService(
                live.DataDirectory,
                live.AssetDatabasePath,
                live.ReaderDatabasePath).ApplyPendingAsync());

        Assert.True(exception.CurrentStateIsSafe);
        Assert.Null(exception.SafetyBackupPath);
        Assert.True(Directory.Exists(live.AssetDatabasePath));
        Assert.Equal("current", await ReadMarkerAsync(live.ReaderDatabasePath));
        Assert.False(Directory.Exists(preparation.SafetyBackupPath));
    }

    [Fact]
    public async Task ApplyPendingAsync_EmergencyRollbackRestoresOriginallyMissingDatabases()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var fixtureRoot = Path.Combine(directory.Path, "Emergency");
        var dataDirectory = Path.Combine(fixtureRoot, "Data");
        var assetDatabasePath = Path.Combine(dataDirectory, "cdsi.db");
        var readerDatabasePath = Path.Combine(dataDirectory, "reader.db");
        Directory.CreateDirectory(dataDirectory);
        var protection = new LocalStateProtectionService(
            dataDirectory,
            assetDatabasePath,
            readerDatabasePath,
            ApplicationVersion);
        var preparation = await protection.PrepareEmergencyRestoreAsync(
            source.BackupPath,
            Guid.NewGuid().ToString("D"));
        var pending = new PendingStateRestoreService(
            dataDirectory,
            assetDatabasePath,
            readerDatabasePath,
            role =>
            {
                if (string.Equals(role, "reader", StringComparison.Ordinal))
                {
                    throw new IOException("Injected reader replacement failure.");
                }
            });

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(
            () => pending.ApplyPendingAsync());

        Assert.True(exception.CurrentStateIsSafe);
        Assert.Equal(preparation.SafetyBackupPath, exception.SafetyBackupPath);
        Assert.False(File.Exists(assetDatabasePath));
        Assert.False(File.Exists(readerDatabasePath));
        Assert.False(File.Exists($"{assetDatabasePath}-wal"));
        Assert.False(File.Exists($"{assetDatabasePath}-shm"));
        Assert.False(File.Exists($"{assetDatabasePath}-journal"));
        Assert.False(File.Exists($"{readerDatabasePath}-wal"));
        Assert.False(File.Exists($"{readerDatabasePath}-shm"));
        Assert.False(File.Exists($"{readerDatabasePath}-journal"));
        Assert.False(pending.HasPendingRestore);
    }

    [Fact]
    public async Task ApplyPendingAsync_DoesNotReportEmergencySafetyBeforeCaptureCompletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var preparation = await live.Protection.PrepareEmergencyRestoreAsync(
            source.BackupPath,
            Guid.NewGuid().ToString("D"));
        await using var lockedDatabase = new FileStream(
            live.AssetDatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(() =>
            new PendingStateRestoreService(
                live.DataDirectory,
                live.AssetDatabasePath,
                live.ReaderDatabasePath).ApplyPendingAsync());

        Assert.True(exception.CurrentStateIsSafe);
        Assert.Null(exception.SafetyBackupPath);
        Assert.False(Directory.Exists(preparation.SafetyBackupPath));
    }

    [Fact]
    public async Task ApplyPendingAsync_RejectsNullRawSafetyManifestEntriesWithoutReportingSafety()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var preparation = await live.Protection.PrepareEmergencyRestoreAsync(
            source.BackupPath,
            Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(preparation.SafetyBackupPath);
        var manifestPath = Path.Combine(
            preparation.SafetyBackupPath,
            "raw-safety.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(
                new RawStateSafetyManifest(
                    StateBundleArchive.CurrentFormatVersion,
                    preparation.RestoreId,
                    DateTimeOffset.UtcNow,
                    new RawStateSafetyFileManifest[]
                    {
                        null!, null!, null!, null!, null!, null!, null!, null!
                    }),
                StateBundleArchive.JsonOptions));
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        var pendingPlanPath = Path.Combine(
            protectionRoot,
            LocalStateProtectionService.PendingPlanFileName);
        var plan = JsonSerializer.Deserialize<PendingStateRestorePlan>(
            await File.ReadAllTextAsync(pendingPlanPath),
            StateBundleArchive.JsonOptions)!;
        await LocalStateProtectionService.WritePendingPlanAsync(
            pendingPlanPath,
            plan with
            {
                RawSafetyManifestSha256 = await StateBundleArchive.ComputeSha256Async(
                    manifestPath,
                    CancellationToken.None),
                Phase = "SafetyCaptured"
            },
            overwrite: true,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(() =>
            new PendingStateRestoreService(
                live.DataDirectory,
                live.AssetDatabasePath,
                live.ReaderDatabasePath).ApplyPendingAsync());

        Assert.False(exception.CurrentStateIsSafe);
        Assert.Null(exception.SafetyBackupPath);
        Assert.IsType<StateBackupValidationException>(exception.InnerException);
    }

    [Fact]
    public async Task ApplyPendingAsync_EmergencyRestoreRollsBackExactRawFileFamilies()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        await File.WriteAllBytesAsync(
            $"{live.AssetDatabasePath}-wal",
            [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(
            $"{live.AssetDatabasePath}-shm",
            [5, 6, 7]);
        await File.WriteAllBytesAsync(
            $"{live.ReaderDatabasePath}-journal",
            [8, 9]);
        var originalFiles = ReadRawFileFamily(live);
        var preparation = await live.Protection.PrepareEmergencyRestoreAsync(
            source.BackupPath,
            Guid.NewGuid().ToString("D"));
        var pending = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath,
            role =>
            {
                if (string.Equals(role, "reader", StringComparison.Ordinal))
                {
                    throw new IOException("Injected reader replacement failure.");
                }
            });

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(
            () => pending.ApplyPendingAsync());

        Assert.True(exception.CurrentStateIsSafe);
        Assert.Equal(preparation.SafetyBackupPath, exception.SafetyBackupPath);
        Assert.False(pending.HasPendingRestore);
        AssertRawFileFamily(live, originalFiles);
        Assert.True(Directory.Exists(preparation.SafetyBackupPath));
    }

    [Fact]
    public async Task ApplyPendingAsync_WhenReaderReplacementFails_RollsBackBothDatabases()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        _ = await live.Protection.PrepareRestoreAsync(
            source.BackupPath,
            live.WorkspacePath,
            Guid.NewGuid().ToString("D"));
        var pending = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath,
            role =>
            {
                if (string.Equals(role, "reader", StringComparison.Ordinal))
                {
                    throw new IOException("Injected reader replacement failure.");
                }
            });

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(
            () => pending.ApplyPendingAsync());

        Assert.True(exception.CurrentStateIsSafe);
        Assert.False(pending.HasPendingRestore);
        Assert.Equal("current", await ReadMarkerAsync(live.AssetDatabasePath));
        Assert.Equal("current", await ReadMarkerAsync(live.ReaderDatabasePath));
    }

    [Fact]
    public async Task ApplyPendingAsync_UsesPrevalidatedSafetyCopyForSameProcessRollback()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var preparation = await live.Protection.PrepareRestoreAsync(
            source.BackupPath,
            live.WorkspacePath,
            Guid.NewGuid().ToString("D"));
        var operationDirectory = LocalStateProtectionService.GetOperationDirectory(
            LocalStateProtectionService.GetProtectionRoot(live.DataDirectory),
            preparation.RestoreId);
        var stagedSafetyBundle = Path.Combine(
            operationDirectory,
            LocalStateProtectionService.SafetyBundleFileName);
        var pending = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath,
            role =>
            {
                if (string.Equals(role, "reader", StringComparison.Ordinal))
                {
                    File.Delete(stagedSafetyBundle);
                    throw new IOException("Injected reader replacement failure.");
                }
            });

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(
            () => pending.ApplyPendingAsync());

        Assert.True(exception.CurrentStateIsSafe);
        Assert.False(pending.HasPendingRestore);
        Assert.Equal("current", await ReadMarkerAsync(live.AssetDatabasePath));
        Assert.Equal("current", await ReadMarkerAsync(live.ReaderDatabasePath));
    }

    [Fact]
    public async Task ApplyPendingAsync_AfterInterruptedAssetReplacement_RollsBackOnce()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var preparation = await live.Protection.PrepareRestoreAsync(
            source.BackupPath,
            live.WorkspacePath,
            Guid.NewGuid().ToString("D"));
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        var operationDirectory = LocalStateProtectionService.GetOperationDirectory(
            protectionRoot,
            preparation.RestoreId);
        var simulatedExtraction = Path.Combine(operationDirectory, "simulated-crash");
        var extracted = await StateBundleArchive.ExtractAndValidateAsync(
            Path.Combine(
                operationDirectory,
                LocalStateProtectionService.RestoreBundleFileName),
            simulatedExtraction,
            CancellationToken.None);
        SqliteConnection.ClearAllPools();
        File.Delete($"{live.AssetDatabasePath}-wal");
        File.Delete($"{live.AssetDatabasePath}-shm");
        File.Delete($"{live.AssetDatabasePath}-journal");
        File.Replace(
            extracted.AssetDatabasePath,
            live.AssetDatabasePath,
            Path.Combine(operationDirectory, "simulated-displaced.db"));
        var pendingPlanPath = Path.Combine(
            protectionRoot,
            LocalStateProtectionService.PendingPlanFileName);
        var plan = JsonSerializer.Deserialize<PendingStateRestorePlan>(
            await File.ReadAllTextAsync(pendingPlanPath),
            StateBundleArchive.JsonOptions)!;
        await LocalStateProtectionService.WritePendingPlanAsync(
            pendingPlanPath,
            plan with { Phase = "AssetApplied" },
            overwrite: true,
            CancellationToken.None);
        var pending = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath);

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(
            () => pending.ApplyPendingAsync());

        Assert.True(exception.CurrentStateIsSafe);
        Assert.Equal("current", await ReadMarkerAsync(live.AssetDatabasePath));
        Assert.Equal("current", await ReadMarkerAsync(live.ReaderDatabasePath));
        Assert.False(pending.HasPendingRestore);
        Assert.Null(await pending.ApplyPendingAsync());
    }

    [Fact]
    public async Task ApplyPendingAsync_PreservesExtractedSafetyWhenInterruptedBundleIsDamaged()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var preparation = await live.Protection.PrepareRestoreAsync(
            source.BackupPath,
            live.WorkspacePath,
            Guid.NewGuid().ToString("D"));
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        var operationDirectory = LocalStateProtectionService.GetOperationDirectory(
            protectionRoot,
            preparation.RestoreId);
        var safetyDirectory = Path.Combine(operationDirectory, "safety");
        _ = await StateBundleArchive.ExtractAndValidateAsync(
            Path.Combine(
                operationDirectory,
                LocalStateProtectionService.SafetyBundleFileName),
            safetyDirectory,
            CancellationToken.None);
        var incomingDirectory = Path.Combine(operationDirectory, "incoming");
        var incoming = await StateBundleArchive.ExtractAndValidateAsync(
            Path.Combine(
                operationDirectory,
                LocalStateProtectionService.RestoreBundleFileName),
            incomingDirectory,
            CancellationToken.None);
        SqliteConnection.ClearAllPools();
        File.Delete($"{live.AssetDatabasePath}-wal");
        File.Delete($"{live.AssetDatabasePath}-shm");
        File.Delete($"{live.AssetDatabasePath}-journal");
        File.Replace(
            incoming.AssetDatabasePath,
            live.AssetDatabasePath,
            Path.Combine(operationDirectory, "simulated-displaced.db"));
        var pendingPlanPath = Path.Combine(
            protectionRoot,
            LocalStateProtectionService.PendingPlanFileName);
        var plan = JsonSerializer.Deserialize<PendingStateRestorePlan>(
            await File.ReadAllTextAsync(pendingPlanPath),
            StateBundleArchive.JsonOptions)!;
        await LocalStateProtectionService.WritePendingPlanAsync(
            pendingPlanPath,
            plan with { Phase = "AssetApplied" },
            overwrite: true,
            CancellationToken.None);
        await File.WriteAllBytesAsync(
            Path.Combine(
                operationDirectory,
                LocalStateProtectionService.SafetyBundleFileName),
            [0x43, 0x44, 0x53, 0x49]);

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(() =>
            new PendingStateRestoreService(
                live.DataDirectory,
                live.AssetDatabasePath,
                live.ReaderDatabasePath).ApplyPendingAsync());

        Assert.False(exception.CurrentStateIsSafe);
        Assert.True(Directory.Exists(safetyDirectory));
        Assert.True(File.Exists(Path.Combine(safetyDirectory, "cdsi.db")));
        Assert.True(File.Exists(Path.Combine(safetyDirectory, "reader.db")));
    }

    [Fact]
    public async Task ApplyPendingAsync_DamagedPlanStopsStartupWithoutChangingDatabases()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        Directory.CreateDirectory(protectionRoot);
        var pendingPlanPath = Path.Combine(
            protectionRoot,
            LocalStateProtectionService.PendingPlanFileName);
        await File.WriteAllTextAsync(pendingPlanPath, "{ not-json }");
        var pending = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath);

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(
            () => pending.ApplyPendingAsync());

        Assert.False(exception.CurrentStateIsSafe);
        Assert.True(pending.HasPendingRestore);
        Assert.Equal("current", await ReadMarkerAsync(live.AssetDatabasePath));
        Assert.Equal("current", await ReadMarkerAsync(live.ReaderDatabasePath));
    }

    [Fact]
    public async Task ApplyPendingAsync_PendingPlanDirectoryStopsStartup()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var pendingPlanPath = Path.Combine(
            LocalStateProtectionService.GetProtectionRoot(live.DataDirectory),
            LocalStateProtectionService.PendingPlanFileName);
        Directory.CreateDirectory(pendingPlanPath);

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(() =>
            new PendingStateRestoreService(
                live.DataDirectory,
                live.AssetDatabasePath,
                live.ReaderDatabasePath).ApplyPendingAsync());

        Assert.False(exception.CurrentStateIsSafe);
        Assert.True(Directory.Exists(pendingPlanPath));
        Assert.Equal("current", await ReadMarkerAsync(live.AssetDatabasePath));
        Assert.Equal("current", await ReadMarkerAsync(live.ReaderDatabasePath));
    }

    [Fact]
    public async Task ApplyPendingAsync_InvalidSafetyPathIsNotReportedToCallers()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        _ = await live.Protection.PrepareRestoreAsync(
            source.BackupPath,
            live.WorkspacePath,
            Guid.NewGuid().ToString("D"));
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        var pendingPlanPath = Path.Combine(
            protectionRoot,
            LocalStateProtectionService.PendingPlanFileName);
        var plan = JsonSerializer.Deserialize<PendingStateRestorePlan>(
            await File.ReadAllTextAsync(pendingPlanPath),
            StateBundleArchive.JsonOptions)!;
        await LocalStateProtectionService.WritePendingPlanAsync(
            pendingPlanPath,
            plan with { SafetyBackupPath = $"{live.DataDirectory}\0unsafe.cdsibak" },
            overwrite: true,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<StateRestoreFailedException>(() =>
            new PendingStateRestoreService(
                live.DataDirectory,
                live.AssetDatabasePath,
                live.ReaderDatabasePath).ApplyPendingAsync());

        Assert.False(exception.CurrentStateIsSafe);
        Assert.Null(exception.SafetyBackupPath);
        Assert.Equal("current", await ReadMarkerAsync(live.AssetDatabasePath));
        Assert.Equal("current", await ReadMarkerAsync(live.ReaderDatabasePath));
    }

    [Fact]
    public async Task ApplyPendingAsync_RetriesDeferredControlledCleanupOnNextStartup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var preparation = await live.Protection.PrepareRestoreAsync(
            source.BackupPath,
            live.WorkspacePath,
            Guid.NewGuid().ToString("D"));
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        var operationDirectory = LocalStateProtectionService.GetOperationDirectory(
            protectionRoot,
            preparation.RestoreId);
        var lockedPath = Path.Combine(operationDirectory, "cleanup-lock.tmp");
        await using (var locked = new FileStream(
                         lockedPath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            var result = await new PendingStateRestoreService(
                live.DataDirectory,
                live.AssetDatabasePath,
                live.ReaderDatabasePath).ApplyPendingAsync();

            Assert.NotNull(result);
            Assert.True(Directory.Exists(operationDirectory));
            Assert.True(File.Exists(Path.Combine(
                protectionRoot,
                "pending-cleanup.json")));
        }

        var nextStartup = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath);
        Assert.Null(await nextStartup.ApplyPendingAsync());
        Assert.False(Directory.Exists(operationDirectory));
        Assert.False(File.Exists(Path.Combine(
            protectionRoot,
            "pending-cleanup.json")));
    }

    [Fact]
    public async Task ApplyPendingAsync_AbandonedTerminalPlanNeverReplaysAfterDeleteFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        _ = await live.Protection.PrepareRestoreAsync(
            source.BackupPath,
            live.WorkspacePath,
            Guid.NewGuid().ToString("D"));
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        var pendingPlanPath = Path.Combine(
            protectionRoot,
            LocalStateProtectionService.PendingPlanFileName);
        var plan = JsonSerializer.Deserialize<PendingStateRestorePlan>(
            await File.ReadAllTextAsync(pendingPlanPath),
            StateBundleArchive.JsonOptions)!;
        await LocalStateProtectionService.WritePendingPlanAsync(
            pendingPlanPath,
            plan with { Phase = "Abandoned" },
            overwrite: true,
            CancellationToken.None);

        File.SetAttributes(
            pendingPlanPath,
            File.GetAttributes(pendingPlanPath) | FileAttributes.ReadOnly);
        try
        {
            var firstStartup = new PendingStateRestoreService(
                live.DataDirectory,
                live.AssetDatabasePath,
                live.ReaderDatabasePath);
            Assert.Null(await firstStartup.ApplyPendingAsync());
            Assert.True(firstStartup.HasPendingRestore);
            await ExecuteSqlAsync(
                live.AssetDatabasePath,
                "UPDATE schema_migrations SET applied_at = 'new-write' WHERE version = 1;");
        }
        finally
        {
            if (File.Exists(pendingPlanPath))
            {
                File.SetAttributes(
                    pendingPlanPath,
                    File.GetAttributes(pendingPlanPath) & ~FileAttributes.ReadOnly);
            }
        }

        var nextStartup = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath);
        Assert.Null(await nextStartup.ApplyPendingAsync());
        Assert.False(nextStartup.HasPendingRestore);
        Assert.Equal("new-write", await ReadMarkerAsync(live.AssetDatabasePath));
    }

    [Fact]
    public async Task ApplyPendingAsync_RawAbandonedPlanAcceptsCapturedSafetyHash()
    {
        using var directory = new TestDirectory();
        var source = await CreateBackupFixtureAsync(
            directory.Path,
            "Source",
            "backup");
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        _ = await live.Protection.PrepareEmergencyRestoreAsync(
            source.BackupPath,
            Guid.NewGuid().ToString("D"));
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        var pendingPlanPath = Path.Combine(
            protectionRoot,
            LocalStateProtectionService.PendingPlanFileName);
        var plan = JsonSerializer.Deserialize<PendingStateRestorePlan>(
            await File.ReadAllTextAsync(pendingPlanPath),
            StateBundleArchive.JsonOptions)!;
        await LocalStateProtectionService.WritePendingPlanAsync(
            pendingPlanPath,
            plan with
            {
                RawSafetyManifestSha256 = new string('A', 64),
                Phase = "Abandoned"
            },
            overwrite: true,
            CancellationToken.None);

        var pending = new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath);
        Assert.Null(await pending.ApplyPendingAsync());
        Assert.False(pending.HasPendingRestore);
        Assert.Equal("current", await ReadMarkerAsync(live.AssetDatabasePath));
        Assert.Equal("current", await ReadMarkerAsync(live.ReaderDatabasePath));
    }

    [Fact]
    public async Task ApplyPendingAsync_RemovesOrphanedControlledWorkDirectories()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        var temporaryOrphan = Path.Combine(
            protectionRoot,
            "Temp",
            "inspect-orphan");
        var pendingOrphan = Path.Combine(
            protectionRoot,
            LocalStateProtectionService.PendingDirectoryName,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryOrphan);
        Directory.CreateDirectory(pendingOrphan);
        await File.WriteAllTextAsync(
            Path.Combine(temporaryOrphan, "cdsi.db"),
            "sensitive");
        await File.WriteAllTextAsync(
            Path.Combine(pendingOrphan, "restore.cdsibak"),
            "sensitive");

        Assert.Null(await new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath).ApplyPendingAsync());

        Assert.False(Directory.Exists(temporaryOrphan));
        Assert.False(Directory.Exists(pendingOrphan));
    }

    [Fact]
    public async Task ApplyPendingAsync_RemovesOnlyStrictOwnedStartupTemporaryFiles()
    {
        using var directory = new TestDirectory();
        var live = await CreateLiveFixtureAsync(
            directory.Path,
            "Live",
            "current");
        var protectionRoot = LocalStateProtectionService.GetProtectionRoot(
            live.DataDirectory);
        Directory.CreateDirectory(protectionRoot);
        var pendingTemporary = Path.Combine(
            protectionRoot,
            $".pending-restore.json.{Guid.NewGuid():N}.tmp");
        var cleanupTemporary = Path.Combine(
            protectionRoot,
            $"pending-cleanup.json.{Guid.NewGuid():N}.tmp");
        var rawRestoreTemporary = Path.Combine(
            live.DataDirectory,
            $".cdsi.db.{Guid.NewGuid():N}.restore.tmp");
        var unrelated = Path.Combine(
            live.DataDirectory,
            ".cdsi.db.not-a-guid.restore.tmp");
        await File.WriteAllTextAsync(pendingTemporary, "sensitive");
        await File.WriteAllTextAsync(cleanupTemporary, "sensitive");
        await File.WriteAllTextAsync(rawRestoreTemporary, "sensitive");
        await File.WriteAllTextAsync(unrelated, "keep");

        Assert.Null(await new PendingStateRestoreService(
            live.DataDirectory,
            live.AssetDatabasePath,
            live.ReaderDatabasePath).ApplyPendingAsync());

        Assert.False(File.Exists(pendingTemporary));
        Assert.False(File.Exists(cleanupTemporary));
        Assert.False(File.Exists(rawRestoreTemporary));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void TryDeleteDirectory_RejectsPathOutsideControlledRoot()
    {
        using var directory = new TestDirectory();
        var controlledRoot = Path.Combine(directory.Path, "Controlled");
        var outside = Path.Combine(directory.Path, "Outside");
        Directory.CreateDirectory(controlledRoot);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");

        var deleted = StateProtectionPathGuard.TryDeleteDirectory(
            controlledRoot,
            outside);

        Assert.False(deleted);
        Assert.True(File.Exists(sentinel));
    }

    private static async Task<BackupFixture> CreateBackupFixtureAsync(
        string rootPath,
        string name,
        string marker)
    {
        var fixture = await CreateLiveFixtureAsync(rootPath, name, marker);
        var backup = await fixture.Protection.CreateBackupAsync(
            fixture.WorkspacePath,
            LocalStateBackupKind.Manual,
            Guid.NewGuid().ToString("D"));
        return new BackupFixture(backup.Path, backup.BackupId!.Value);
    }

    private static async Task<LiveFixture> CreateLiveFixtureAsync(
        string rootPath,
        string name,
        string marker)
    {
        var fixtureRoot = Path.Combine(rootPath, name);
        var dataDirectory = Path.Combine(fixtureRoot, "Data");
        var workspacePath = Path.Combine(fixtureRoot, "Workspace");
        var assetDatabasePath = Path.Combine(dataDirectory, "cdsi.db");
        var readerDatabasePath = Path.Combine(dataDirectory, "reader.db");
        await CreateStateDatabasesAsync(
            assetDatabasePath,
            readerDatabasePath,
            marker);
        return new LiveFixture(
            dataDirectory,
            workspacePath,
            assetDatabasePath,
            readerDatabasePath,
            new LocalStateProtectionService(
                dataDirectory,
                assetDatabasePath,
                readerDatabasePath,
                ApplicationVersion));
    }

    private static async Task CreateStateDatabasesAsync(
        string assetDatabasePath,
        string readerDatabasePath,
        string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(assetDatabasePath)!);
        await DatabaseMigrator.MigrateAsync(
            CreateConnectionString(assetDatabasePath, SqliteOpenMode.ReadWriteCreate),
            CancellationToken.None);
        await ReaderDatabaseMigrator.MigrateAsync(
            CreateConnectionString(readerDatabasePath, SqliteOpenMode.ReadWriteCreate),
            CancellationToken.None);
        await WriteMarkerAsync(assetDatabasePath, marker);
        await WriteMarkerAsync(readerDatabasePath, marker);
        SqliteConnection.ClearAllPools();
    }

    private static async Task WriteMarkerAsync(string databasePath, string marker)
    {
        await using var connection = new SqliteConnection(
            CreateConnectionString(databasePath, SqliteOpenMode.ReadWrite));
        await connection.OpenAsync();
        var migrationTable = await FindMigrationTableAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = migrationTable switch
        {
            "schema_migrations" =>
                "UPDATE schema_migrations SET applied_at = $value WHERE version = 1;",
            "reader_schema_migrations" =>
                "UPDATE reader_schema_migrations SET applied_at = $value WHERE version = 1;",
            _ => throw new InvalidOperationException("Unknown migration table.")
        };
        command.Parameters.AddWithValue("$value", marker);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(
            CreateConnectionString(databasePath, SqliteOpenMode.ReadWrite));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task RewriteSchemaSqlAsync(
        string databasePath,
        string schemaObjectType,
        string schemaObjectName,
        string oldSql,
        string newSql)
    {
        await using var connection = new SqliteConnection(
            CreateConnectionString(databasePath, SqliteOpenMode.ReadWrite));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA writable_schema = ON;
            UPDATE sqlite_schema
            SET sql = replace(sql, $old_sql, $new_sql)
            WHERE type = $type AND name = $name;
            PRAGMA writable_schema = OFF;
            """;
        command.Parameters.AddWithValue("$old_sql", oldSql);
        command.Parameters.AddWithValue("$new_sql", newSql);
        command.Parameters.AddWithValue("$type", schemaObjectType);
        command.Parameters.AddWithValue("$name", schemaObjectName);
        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }

    private static Dictionary<string, byte[]?> ReadRawFileFamily(LiveFixture live)
    {
        string[] paths =
        [
            live.AssetDatabasePath,
            $"{live.AssetDatabasePath}-wal",
            $"{live.AssetDatabasePath}-shm",
            $"{live.AssetDatabasePath}-journal",
            live.ReaderDatabasePath,
            $"{live.ReaderDatabasePath}-wal",
            $"{live.ReaderDatabasePath}-shm",
            $"{live.ReaderDatabasePath}-journal"
        ];
        return paths.ToDictionary(
            path => path,
            path => File.Exists(path) ? File.ReadAllBytes(path) : null,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertRawFileFamily(
        LiveFixture live,
        IReadOnlyDictionary<string, byte[]?> expected)
    {
        var actual = ReadRawFileFamily(live);
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (path, bytes) in expected)
        {
            if (bytes is null)
            {
                Assert.False(File.Exists(path));
            }
            else
            {
                Assert.Equal(bytes, actual[path]);
            }
        }
    }

    private static async Task<string> ReadMarkerAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            CreateConnectionString(databasePath, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync();
        var migrationTable = await FindMigrationTableAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = migrationTable switch
        {
            "schema_migrations" =>
                "SELECT applied_at FROM schema_migrations WHERE version = 1;",
            "reader_schema_migrations" =>
                "SELECT applied_at FROM reader_schema_migrations WHERE version = 1;",
            _ => throw new InvalidOperationException("Unknown migration table.")
        };
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> FindMigrationTableAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table'
              AND name IN ('schema_migrations', 'reader_schema_migrations')
            LIMIT 1;
            """;
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static string CreateConnectionString(
        string databasePath,
        SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString();

    private sealed record BackupFixture(string BackupPath, Guid BackupId);

    private sealed record LiveFixture(
        string DataDirectory,
        string WorkspacePath,
        string AssetDatabasePath,
        string ReaderDatabasePath,
        LocalStateProtectionService Protection);
}
