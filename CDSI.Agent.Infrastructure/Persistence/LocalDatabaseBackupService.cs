using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed class LocalDatabaseBackupService
{
    private const int RecentSnapshotCount = 24;
    private const int DailySnapshotCount = 14;
    private const int MonthlySnapshotCount = 12;
    private const string SnapshotPrefix = "cdsi-";
    private const string SnapshotTimestampFormat = "yyyyMMdd-HHmmss-fff'Z'";

    private readonly string _databasePath;
    private readonly string _applicationVersion;
    private readonly string? _backupSubdirectory;
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    public LocalDatabaseBackupService(
        string databasePath,
        string applicationVersion,
        string? backupSubdirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        _databasePath = Path.GetFullPath(databasePath);
        _applicationVersion = applicationVersion.Trim();
        if (!string.IsNullOrWhiteSpace(backupSubdirectory) &&
            (backupSubdirectory.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
             backupSubdirectory is "." or ".."))
        {
            throw new ArgumentException(
                "Backup subdirectory must be a single directory name.",
                nameof(backupSubdirectory));
        }

        _backupSubdirectory = string.IsNullOrWhiteSpace(backupSubdirectory)
            ? null
            : backupSubdirectory.Trim();
    }

    public string GetBackupDirectory(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var root = Path.Combine(
            Path.GetFullPath(workspacePath),
            "System",
            "DatabaseBackups");
        return _backupSubdirectory is null
            ? root
            : Path.Combine(root, _backupSubdirectory);
    }

    public async Task<LocalDatabaseBackupResult> CreateSnapshotAsync(
        string workspacePath,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _backupLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_databasePath))
            {
                throw new FileNotFoundException("Beacon SQLite 数据库不存在。", _databasePath);
            }

            var normalizedWorkspacePath = Path.GetFullPath(workspacePath);
            var backupDirectory = GetBackupDirectory(normalizedWorkspacePath);
            EnsureBackupDirectory(normalizedWorkspacePath, backupDirectory);
            DeleteTemporaryFiles(backupDirectory);

            var sourceInfo = new FileInfo(_databasePath);
            var sourceStamp = new SourceDatabaseStamp(
                sourceInfo.Length,
                sourceInfo.LastWriteTimeUtc);

            var createdAtUtc = DateTimeOffset.UtcNow;
            var snapshotPath = CreateAvailableSnapshotPath(
                backupDirectory,
                createdAtUtc);
            var manifestPath = GetManifestPath(snapshotPath);
            var temporarySnapshotPath = Path.Combine(
                backupDirectory,
                $".{Path.GetFileName(snapshotPath)}.{Guid.NewGuid():N}.tmp");
            var temporaryManifestPath = $"{temporarySnapshotPath}.json";

            try
            {
                await CreateConsistentCopyAsync(
                    temporarySnapshotPath,
                    cancellationToken);
                var sqliteVersion = await VerifySnapshotAsync(
                    temporarySnapshotPath,
                    cancellationToken);
                var snapshotInfo = new FileInfo(temporarySnapshotPath);
                var sha256 = await ComputeSha256Async(
                    temporarySnapshotPath,
                    cancellationToken);
                if (!force)
                {
                    var latest = await ReadLatestValidSnapshotAsync(
                        backupDirectory,
                        cancellationToken);
                    if (latest is not null && string.Equals(
                            latest.Manifest.Sha256,
                            sha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return new LocalDatabaseBackupResult(
                            backupDirectory,
                            latest.SnapshotPath,
                            Created: false,
                            latest.Manifest.CreatedAtUtc);
                    }
                }

                var manifest = new LocalDatabaseBackupManifest(
                    FormatVersion: 1,
                    CreatedAtUtc: createdAtUtc,
                    ApplicationVersion: _applicationVersion,
                    SqliteVersion: sqliteVersion,
                    DatabaseFileName: Path.GetFileName(snapshotPath),
                    DatabaseSize: snapshotInfo.Length,
                    Sha256: sha256,
                    SourceLength: sourceStamp.Length,
                    SourceLastWriteTimeUtc: sourceStamp.LastWriteTimeUtc);

                await WriteManifestAsync(
                    temporaryManifestPath,
                    manifest,
                    cancellationToken);
                File.Move(temporarySnapshotPath, snapshotPath);
                File.Move(temporaryManifestPath, manifestPath);
                File.SetLastWriteTimeUtc(snapshotPath, createdAtUtc.UtcDateTime);
                File.SetLastWriteTimeUtc(manifestPath, createdAtUtc.UtcDateTime);

                PruneSnapshots(backupDirectory);
                return new LocalDatabaseBackupResult(
                    backupDirectory,
                    snapshotPath,
                    Created: true,
                    createdAtUtc);
            }
            finally
            {
                TryDelete(temporarySnapshotPath);
                TryDelete(temporaryManifestPath);
            }
        }
        finally
        {
            _backupLock.Release();
        }
    }

    internal async Task CreateVerifiedSnapshotFileAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(
                destination,
                _databasePath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Snapshot destination must differ from the source database.",
                nameof(destinationPath));
        }

        await _backupLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_databasePath))
            {
                throw new FileNotFoundException(
                    "Beacon SQLite 数据库不存在。",
                    _databasePath);
            }

            if (File.Exists(destination))
            {
                throw new IOException($"快照目标文件已存在：{destination}");
            }

            var directory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("快照路径没有父目录。");
            Directory.CreateDirectory(directory);
            try
            {
                await CreateConsistentCopyAsync(destination, cancellationToken);
                await VerifySnapshotAsync(destination, cancellationToken);
            }
            catch
            {
                TryDelete(destination);
                throw;
            }
        }
        finally
        {
            _backupLock.Release();
        }
    }

    private async Task CreateConsistentCopyAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 10
                }.ToString();
                var destinationConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = destinationPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                    DefaultTimeout = 10
                }.ToString();
                using var source = new SqliteConnection(sourceConnectionString);
                using var destination = new SqliteConnection(destinationConnectionString);
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            },
            cancellationToken);
    }

    private static async Task<string> VerifySnapshotAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(
                await quickCheck.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SQLite 快照完整性检查失败: {result ?? "无结果"}");
            }
        }

        await using (var foreignKeyCheck = connection.CreateCommand())
        {
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeyCheck.ExecuteReaderAsync(
                cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException("SQLite 快照存在外键约束错误。");
            }
        }

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(
                await versionCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture)
            ?? "unknown";
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static async Task WriteManifestAsync(
        string path,
        LocalDatabaseBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            manifest,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<LatestSnapshot?> ReadLatestValidSnapshotAsync(
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var snapshotPath in EnumerateSnapshots(backupDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifestPath = GetManifestPath(snapshotPath);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var manifest = JsonSerializer.Deserialize<LocalDatabaseBackupManifest>(
                    File.ReadAllText(manifestPath));
                if (manifest is null ||
                    !string.Equals(
                        manifest.DatabaseFileName,
                        Path.GetFileName(snapshotPath),
                        StringComparison.OrdinalIgnoreCase) ||
                    manifest.DatabaseSize != new FileInfo(snapshotPath).Length)
                {
                    continue;
                }

                var actualSha256 = await ComputeSha256Async(
                    snapshotPath,
                    cancellationToken);
                if (!string.Equals(
                        manifest.Sha256,
                        actualSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await VerifySnapshotAsync(snapshotPath, cancellationToken);
                return new LatestSnapshot(
                    snapshotPath,
                    manifest,
                    new SourceDatabaseStamp(
                        manifest.SourceLength,
                        manifest.SourceLastWriteTimeUtc));
            }
            catch (IOException)
            {
                // A damaged sidecar must not block creation of a fresh snapshot.
            }
            catch (JsonException)
            {
                // A damaged sidecar must not block creation of a fresh snapshot.
            }
            catch (InvalidDataException)
            {
                // A damaged snapshot must not block creation of a fresh snapshot.
            }
            catch (SqliteException)
            {
                // A damaged snapshot must not block creation of a fresh snapshot.
            }
            catch (UnauthorizedAccessException)
            {
                // An unreadable snapshot must not block creation of a fresh snapshot.
            }
        }

        return null;
    }

    private static void PruneSnapshots(string backupDirectory)
    {
        var snapshots = EnumerateSnapshots(backupDirectory)
            .Select(path => new SnapshotFile(
                path,
                File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(snapshot => snapshot.CreatedAtUtc)
            .ToArray();
        if (snapshots.Length <= RecentSnapshotCount)
        {
            return;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots.Take(RecentSnapshotCount))
        {
            keep.Add(snapshot.Path);
        }

        foreach (var snapshot in snapshots
                     .GroupBy(item => item.CreatedAtUtc.Date)
                     .OrderByDescending(group => group.Key)
                     .Take(DailySnapshotCount)
                     .Select(group => group.First()))
        {
            keep.Add(snapshot.Path);
        }

        foreach (var snapshot in snapshots
                     .GroupBy(item => (item.CreatedAtUtc.Year, item.CreatedAtUtc.Month))
                     .OrderByDescending(group => group.Key.Year)
                     .ThenByDescending(group => group.Key.Month)
                     .Take(MonthlySnapshotCount)
                     .Select(group => group.First()))
        {
            keep.Add(snapshot.Path);
        }

        foreach (var snapshot in snapshots.Where(item => !keep.Contains(item.Path)))
        {
            TryDelete(snapshot.Path);
            TryDelete(GetManifestPath(snapshot.Path));
        }
    }

    private static IEnumerable<string> EnumerateSnapshots(string backupDirectory)
    {
        return Directory
            .EnumerateFiles(
                backupDirectory,
                $"{SnapshotPrefix}*.db",
                SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
    }

    private static string CreateAvailableSnapshotPath(
        string backupDirectory,
        DateTimeOffset createdAtUtc)
    {
        var timestamp = createdAtUtc.UtcDateTime.ToString(
            SnapshotTimestampFormat,
            CultureInfo.InvariantCulture);
        var candidate = Path.Combine(
            backupDirectory,
            $"{SnapshotPrefix}{timestamp}.db");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        return Path.Combine(
            backupDirectory,
            $"{SnapshotPrefix}{timestamp}-{Guid.NewGuid():N}.db");
    }

    private static string GetManifestPath(string snapshotPath) =>
        $"{snapshotPath}.json";

    private static void EnsureBackupDirectory(
        string workspacePath,
        string backupDirectory)
    {
        var current = new DirectoryInfo(backupDirectory);
        while (current is not null)
        {
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "工作目录及数据库备份目录不能包含符号链接或 junction。");
            }

            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    workspacePath.TrimEnd(Path.DirectorySeparatorChar),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                break;
            }

            current = current.Parent;
        }

        Directory.CreateDirectory(backupDirectory);
        if ((File.GetAttributes(backupDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("数据库备份目录不能是符号链接或 junction。");
        }
    }

    private static void DeleteTemporaryFiles(string backupDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(
                     backupDirectory,
                     ".cdsi-*.tmp*",
                     SearchOption.TopDirectoryOnly))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record LatestSnapshot(
        string SnapshotPath,
        LocalDatabaseBackupManifest Manifest,
        SourceDatabaseStamp Source);

    private sealed record SnapshotFile(string Path, DateTime CreatedAtUtc);

    private sealed record SourceDatabaseStamp(long Length, DateTime LastWriteTimeUtc);
}

public sealed record LocalDatabaseBackupResult(
    string BackupDirectory,
    string SnapshotPath,
    bool Created,
    DateTimeOffset CreatedAtUtc);

public sealed record LocalDatabaseBackupManifest(
    int FormatVersion,
    DateTimeOffset CreatedAtUtc,
    string ApplicationVersion,
    string SqliteVersion,
    string DatabaseFileName,
    long DatabaseSize,
    string Sha256,
    long SourceLength,
    DateTime SourceLastWriteTimeUtc);
