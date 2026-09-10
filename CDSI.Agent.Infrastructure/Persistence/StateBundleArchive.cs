using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

internal static class StateBundleArchive
{
    internal const string FormatName = "cdsi-beacon-state";
    internal const int CurrentFormatVersion = 1;
    internal const string ManifestEntryName = "manifest.json";
    internal const string AssetDatabaseEntryName = "databases/cdsi.db";
    internal const string ReaderDatabaseEntryName = "databases/reader.db";

    internal const long MaximumArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumDatabaseBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumManifestBytes = 256L * 1024;
    private static readonly IReadOnlyDictionary<string, string[]> AssetSchemaContract =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["schema_migrations"] = ["version", "applied_at"],
            ["devices"] = ["id", "name", "platform", "created_at"],
            ["scan_roots"] =
            [
                "id", "path", "path_key", "enabled", "created_at",
                "last_scanned_at", "mode", "status", "updated_at", "removed_at",
                "volume_id", "volume_relative_path", "file_type_filter",
                "extension_whitelist_json", "file_type_filters_json",
                "idle_scan_enabled", "idle_scan_interval", "idle_scan_unit"
            ],
            ["scan_jobs"] =
            [
                "id", "scan_root_id", "status", "started_at", "finished_at",
                "files_discovered", "files_processed", "errors", "error_message"
            ],
            ["assets"] =
            [
                "id", "original_filename", "mime_type", "extension", "size",
                "sha256", "created_at", "modified_at", "discovered_at", "status",
                "hidden_from_asset_list", "hidden_from_asset_list_at"
            ],
            ["asset_locations"] =
            [
                "id", "asset_id", "location_type", "device_id", "path", "path_key",
                "status", "last_seen_at", "last_verified_at", "ownership", "volume_id",
                "volume_relative_path", "excluded_from_asset_list",
                "excluded_from_asset_list_at"
            ],
            ["asset_metadata"] =
            [
                "asset_id", "extractor_name", "pipeline_version", "status",
                "source_size", "source_modified_at", "metadata_json", "error_message",
                "extracted_at"
            ],
            ["managed_workspaces"] =
            [
                "id", "device_id", "path", "path_key", "created_at", "updated_at",
                "volume_id", "volume_relative_path"
            ],
            ["local_volumes"] =
            [
                "id", "stable_id", "serial_number", "label", "filesystem", "drive_type",
                "mount_path", "mount_path_key", "is_online", "first_seen_at",
                "last_seen_at", "updated_at"
            ],
            ["asset_collections"] =
                ["id", "name", "type", "created_at", "updated_at", "backup_profile_id"],
            ["asset_collection_items"] = ["collection_id", "asset_id", "added_at"],
            ["asset_collection_backup_profiles"] =
                ["collection_id", "profile_id", "added_at"],
            ["asset_tags"] =
                ["id", "name", "normalized_name", "created_at", "updated_at"],
            ["asset_tag_links"] = ["asset_id", "tag_id", "tagged_at"],
            ["storage_profiles"] =
            [
                "id", "display_name", "provider", "endpoint", "bucket_name", "region",
                "use_https", "access_key_id", "created_at", "updated_at"
            ],
            ["object_storage_locations"] =
            [
                "id", "asset_id", "storage_profile_id", "object_key", "status", "size",
                "sha256", "etag", "created_at", "updated_at", "last_verified_at"
            ],
            ["file_operations"] =
            [
                "id", "action", "status", "started_at", "finished_at", "total_items",
                "completed_items", "failed_items", "error_message"
            ],
            ["file_operation_items"] =
            [
                "id", "operation_id", "asset_id", "source_path", "target_path", "status",
                "source_deleted", "sha256", "error_message", "finished_at"
            ],
            ["upload_jobs"] =
            [
                "id", "storage_profile_id", "status", "started_at", "finished_at",
                "total_items", "completed_items", "failed_items", "total_bytes",
                "uploaded_bytes", "error_message"
            ],
            ["upload_items"] =
            [
                "id", "job_id", "asset_id", "source_path", "object_key", "status", "size",
                "uploaded_bytes", "etag", "error_message", "finished_at"
            ],
            ["multipart_upload_sessions"] =
            [
                "storage_profile_id", "object_key", "asset_id", "source_path", "upload_id",
                "part_size", "source_size", "source_modified_at", "parts_json", "updated_at"
            ],
            ["restore_jobs"] =
            [
                "id", "status", "destination_kind", "target_directory", "started_at",
                "finished_at", "total_items", "completed_items", "failed_items",
                "total_bytes", "downloaded_bytes", "error_message"
            ],
            ["restore_items"] =
            [
                "id", "job_id", "asset_id", "storage_profile_id", "object_key",
                "target_path", "status", "size", "downloaded_bytes", "sha256",
                "error_message", "finished_at"
            ],
            ["agent_settings"] = ["setting_key", "setting_value", "updated_at"],
            ["openweb_sources"] =
            [
                "id", "display_name", "origin_domain", "wordpress_username", "is_default",
                "created_at", "updated_at"
            ],
            ["openweb_publications"] =
            [
                "asset_id", "publisher", "origin_domain", "remote_post_id",
                "remote_url", "remote_status", "content_sha256", "synchronized_at"
            ],
            ["asset_directory_exclusions"] =
                ["path_key", "path", "path_prefix", "excluded_at"],
            ["asset_collection_deletion_audit"] =
                ["id", "collection_id", "name", "type", "asset_count", "deleted_at"],
            ["git_profiles"] =
            [
                "id", "display_name", "provider", "repository_url", "account_name",
                "default_branch", "is_default", "created_at", "updated_at",
                "authentication_method", "ssh_public_key_path"
            ],
            ["git_project_syncs"] =
            [
                "project_id", "profile_id", "project_name", "project_type", "profile_name",
                "provider", "repository_url", "branch", "commit_id", "synced_files",
                "synced_bytes", "created_commit", "synced_at"
            ]
        };
    private static readonly IReadOnlyDictionary<string, string[]> ReaderSchemaContract =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["reader_schema_migrations"] = ["version", "applied_at"],
            ["reader_feeds"] =
            [
                "id", "title", "feed_url", "feed_url_key", "site_url", "description",
                "feed_type", "folder_name", "etag", "last_modified", "last_checked_at",
                "last_success_at", "last_entry_at", "created_at", "is_enabled",
                "allow_private_network", "last_error"
            ],
            ["reader_entries"] =
            [
                "id", "feed_id", "dedupe_key", "external_id", "title", "url", "author",
                "summary", "content", "published_at", "updated_at", "fetched_at"
            ],
            ["reader_entry_states"] =
                ["entry_id", "is_read", "is_starred", "read_at", "starred_at"],
            ["reader_fetch_logs"] =
            [
                "id", "feed_id", "started_at", "finished_at", "http_status", "result",
                "new_entries", "response_bytes", "duration_ms", "error"
            ]
        };
    private static readonly IReadOnlyDictionary<string, string[]> PrimaryKeyContract =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["schema_migrations"] = ["version"],
            ["devices"] = ["id"],
            ["scan_roots"] = ["id"],
            ["scan_jobs"] = ["id"],
            ["assets"] = ["id"],
            ["asset_locations"] = ["id"],
            ["asset_metadata"] = ["asset_id"],
            ["managed_workspaces"] = ["id"],
            ["local_volumes"] = ["id"],
            ["asset_collections"] = ["id"],
            ["asset_collection_items"] = ["collection_id", "asset_id"],
            ["asset_collection_backup_profiles"] = ["collection_id", "profile_id"],
            ["asset_tags"] = ["id"],
            ["asset_tag_links"] = ["asset_id", "tag_id"],
            ["storage_profiles"] = ["id"],
            ["object_storage_locations"] = ["id"],
            ["file_operations"] = ["id"],
            ["file_operation_items"] = ["id"],
            ["upload_jobs"] = ["id"],
            ["upload_items"] = ["id"],
            ["multipart_upload_sessions"] = ["storage_profile_id", "object_key"],
            ["restore_jobs"] = ["id"],
            ["restore_items"] = ["id"],
            ["agent_settings"] = ["setting_key"],
            ["openweb_sources"] = ["id"],
            ["openweb_publications"] = ["asset_id", "publisher", "origin_domain"],
            ["asset_directory_exclusions"] = ["path_key"],
            ["asset_collection_deletion_audit"] = ["id"],
            ["git_profiles"] = ["id"],
            ["git_project_syncs"] = ["project_id", "profile_id"],
            ["reader_schema_migrations"] = ["version"],
            ["reader_feeds"] = ["id"],
            ["reader_entries"] = ["id"],
            ["reader_entry_states"] = ["entry_id"],
            ["reader_fetch_logs"] = ["id"]
        };
    private static readonly IReadOnlyDictionary<string, string[][]> UniqueKeyContract =
        new Dictionary<string, string[][]>(StringComparer.Ordinal)
        {
            ["scan_roots"] = [["path_key"]],
            ["asset_locations"] = [["device_id", "path_key"]],
            ["managed_workspaces"] = [["device_id"]],
            ["local_volumes"] = [["stable_id"]],
            ["asset_collections"] = [["name"]],
            ["asset_tags"] = [["normalized_name"]],
            ["object_storage_locations"] = [["storage_profile_id", "object_key"]],
            ["openweb_sources"] = [["origin_domain"], ["is_default"]],
            ["git_profiles"] = [["provider", "repository_url"], ["is_default"]],
            ["reader_feeds"] = [["feed_url_key"]],
            ["reader_entries"] = [["feed_id", "dedupe_key"]]
        };
    private static readonly IReadOnlyDictionary<string, string[]> CheckConstraintContract =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["scan_roots"] =
            [
                "CHECK(IDLE_SCAN_INTERVALBETWEEN1AND999)",
                "CHECK(IDLE_SCAN_UNITIN('Minutes','Hours','Days'))"
            ],
            ["openweb_sources"] = ["CHECK(IS_DEFAULTIN(0,1))"],
            ["git_profiles"] = ["CHECK(IS_DEFAULTIN(0,1))"],
            ["git_project_syncs"] =
            [
                "CHECK(SYNCED_FILES>=0)",
                "CHECK(SYNCED_BYTES>=0)",
                "CHECK(CREATED_COMMITIN(0,1))"
            ],
            ["reader_feeds"] =
            [
                "CHECK(IS_ENABLEDIN(0,1))",
                "CHECK(ALLOW_PRIVATE_NETWORKIN(0,1))"
            ],
            ["reader_entry_states"] =
            [
                "CHECK(IS_READIN(0,1))",
                "CHECK(IS_STARREDIN(0,1))"
            ]
        };
    private static readonly IReadOnlyDictionary<string, SchemaForeignKey[]> ForeignKeyContract =
        new Dictionary<string, SchemaForeignKey[]>(StringComparer.Ordinal)
        {
            ["scan_jobs"] = [new("scan_root_id", "scan_roots", "id", "NO ACTION")],
            ["asset_locations"] =
            [
                new("asset_id", "assets", "id", "NO ACTION"),
                new("device_id", "devices", "id", "NO ACTION")
            ],
            ["asset_metadata"] = [new("asset_id", "assets", "id", "CASCADE")],
            ["managed_workspaces"] = [new("device_id", "devices", "id", "NO ACTION")],
            ["asset_collections"] =
                [new("backup_profile_id", "storage_profiles", "id", "SET NULL")],
            ["asset_collection_items"] =
            [
                new("collection_id", "asset_collections", "id", "CASCADE"),
                new("asset_id", "assets", "id", "CASCADE")
            ],
            ["asset_collection_backup_profiles"] =
            [
                new("collection_id", "asset_collections", "id", "CASCADE"),
                new("profile_id", "storage_profiles", "id", "CASCADE")
            ],
            ["asset_tag_links"] =
            [
                new("asset_id", "assets", "id", "CASCADE"),
                new("tag_id", "asset_tags", "id", "CASCADE")
            ],
            ["object_storage_locations"] =
                [new("asset_id", "assets", "id", "NO ACTION")],
            ["file_operation_items"] =
            [
                new("operation_id", "file_operations", "id", "CASCADE"),
                new("asset_id", "assets", "id", "NO ACTION")
            ],
            ["upload_items"] =
            [
                new("job_id", "upload_jobs", "id", "CASCADE"),
                new("asset_id", "assets", "id", "NO ACTION")
            ],
            ["multipart_upload_sessions"] =
                [new("asset_id", "assets", "id", "NO ACTION")],
            ["restore_items"] =
            [
                new("job_id", "restore_jobs", "id", "CASCADE"),
                new("asset_id", "assets", "id", "NO ACTION")
            ],
            ["openweb_publications"] = [new("asset_id", "assets", "id", "CASCADE")],
            ["reader_entries"] = [new("feed_id", "reader_feeds", "id", "CASCADE")],
            ["reader_entry_states"] =
                [new("entry_id", "reader_entries", "id", "CASCADE")],
            ["reader_fetch_logs"] = [new("feed_id", "reader_feeds", "id", "CASCADE")]
        };
    private static readonly Lazy<Task<RuntimeSchemaContract>>
        AssetRuntimeSchemaContract = new(
            () => CreateRuntimeSchemaContractAsync("asset"),
            LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Task<RuntimeSchemaContract>>
        ReaderRuntimeSchemaContract = new(
            () => CreateRuntimeSchemaContractAsync("reader"),
            LazyThreadSafetyMode.ExecutionAndPublication);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static async Task<StateBundleDatabaseManifest> CreateDatabaseManifestAsync(
        string role,
        string entryPath,
        string databasePath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var size = new FileInfo(fullPath).Length;
        if (size <= 0 || size > MaximumDatabaseBytes)
        {
            throw new StateBackupValidationException(
                $"状态数据库大小无效：{Path.GetFileName(fullPath)}。");
        }

        var schemaVersion = await ValidateSqliteDatabaseAsync(
            fullPath,
            role,
            expectedSchemaVersion: null,
            cancellationToken);
        var sha256 = await ComputeSha256Async(fullPath, cancellationToken);
        return new StateBundleDatabaseManifest(
            role,
            entryPath,
            Required: true,
            schemaVersion,
            size,
            sha256);
    }

    internal static async Task CreateAsync(
        string destinationPath,
        StateBundleManifest manifest,
        string assetDatabasePath,
        string readerDatabasePath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("状态备份路径没有父目录。");
        Directory.CreateDirectory(directory);
        if (!overwrite && File.Exists(destination))
        {
            throw new IOException($"状态备份已存在：{destination}");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(
                           file,
                           ZipArchiveMode.Create,
                           leaveOpen: true))
                {
                    await WriteJsonEntryAsync(
                        archive,
                        ManifestEntryName,
                        manifest,
                        cancellationToken);
                    await WriteFileEntryAsync(
                        archive,
                        AssetDatabaseEntryName,
                        assetDatabasePath,
                        cancellationToken);
                    await WriteFileEntryAsync(
                        archive,
                        ReaderDatabaseEntryName,
                        readerDatabasePath,
                        cancellationToken);
                }

                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            EnsureArchiveLengthWithinLimit(temporaryPath);
            File.Move(temporaryPath, destination, overwrite);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    internal static async Task<ExtractedStateBundle> ExtractAndValidateAsync(
        string bundlePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var bundle = Path.GetFullPath(bundlePath);
        if (!File.Exists(bundle))
        {
            throw new FileNotFoundException("状态备份文件不存在。", bundle);
        }

        EnsureArchiveLengthWithinLimit(bundle);

        var extractionRoot = Path.GetFullPath(destinationDirectory);
        if (Directory.Exists(extractionRoot) &&
            Directory.EnumerateFileSystemEntries(extractionRoot).Any())
        {
            throw new InvalidOperationException("状态备份验证目录必须为空。");
        }

        Directory.CreateDirectory(extractionRoot);
        await using var file = new FileStream(
            bundle,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        var entries = ValidateArchiveEntries(archive);
        var manifest = await ReadManifestAsync(
            entries[ManifestEntryName],
            cancellationToken);
        ValidateManifest(manifest);

        var databases = manifest.Databases.ToDictionary(
            database => database.Role,
            StringComparer.Ordinal);
        var assetManifest = databases["asset"];
        var readerManifest = databases["reader"];
        var assetPath = Path.Combine(extractionRoot, "cdsi.db");
        var readerPath = Path.Combine(extractionRoot, "reader.db");
        await ExtractDatabaseAsync(
            entries[AssetDatabaseEntryName],
            assetManifest,
            assetPath,
            cancellationToken);
        await ExtractDatabaseAsync(
            entries[ReaderDatabaseEntryName],
            readerManifest,
            readerPath,
            cancellationToken);

        return new ExtractedStateBundle(manifest, assetPath, readerPath);
    }

    internal static void EnsureArchiveLengthWithinLimit(
        string path,
        long maximumBytes = MaximumArchiveBytes)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var archiveSize = new FileInfo(path).Length;
        if (archiveSize <= 0 || archiveSize > maximumBytes)
        {
            throw new StateBackupValidationException("状态备份文件大小超过安全限制。");
        }
    }

    internal static async Task<string> ComputeSha256Async(
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

    private static Dictionary<string, ZipArchiveEntry> ValidateArchiveEntries(
        ZipArchive archive)
    {
        string[] requiredEntries =
        [
            ManifestEntryName,
            AssetDatabaseEntryName,
            ReaderDatabaseEntryName
        ];
        if (archive.Entries.Count != requiredEntries.Length)
        {
            throw new StateBackupValidationException(
                "状态备份内容不完整或包含当前版本不支持的文件。");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            ValidateEntryName(entry.FullName);
            if (!entries.TryAdd(entry.FullName, entry))
            {
                throw new StateBackupValidationException("状态备份包含重复文件名。");
            }
        }

        foreach (var required in requiredEntries)
        {
            if (!entries.ContainsKey(required))
            {
                throw new StateBackupValidationException(
                    "状态备份不完整，必须同时包含资产数据库和 RSS订阅数据库。");
            }
        }

        return entries;
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains('\\') ||
            name.Contains(':') ||
            name.StartsWith("/", StringComparison.Ordinal) ||
            name.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new StateBackupValidationException("状态备份包含不安全的文件路径。");
        }
    }

    private static async Task<StateBundleManifest> ReadManifestAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length <= 0 || entry.Length > MaximumManifestBytes)
        {
            throw new StateBackupValidationException("状态备份清单大小无效。");
        }

        try
        {
            await using var stream = entry.Open();
            await using var bounded = new MemoryStream(
                checked((int)Math.Min(entry.Length, MaximumManifestBytes)));
            _ = await CopyBoundedAsync(
                stream,
                bounded,
                entry.Length,
                MaximumManifestBytes,
                cancellationToken);
            bounded.Position = 0;
            return await JsonSerializer.DeserializeAsync<StateBundleManifest>(
                       bounded,
                       JsonOptions,
                       cancellationToken)
                   ?? throw new StateBackupValidationException("状态备份清单为空。");
        }
        catch (JsonException exception)
        {
            throw new StateBackupValidationException("状态备份清单格式无效。", exception);
        }
    }

    private static void ValidateManifest(StateBundleManifest manifest)
    {
        if (!string.Equals(manifest.Format, FormatName, StringComparison.Ordinal) ||
            manifest.FormatVersion <= 0)
        {
            throw new StateBackupValidationException("状态备份格式无效或不受支持。");
        }

        if (manifest.FormatVersion > CurrentFormatVersion)
        {
            throw new StateBackupNewerVersionException(
                "此状态备份格式不受当前版本支持，请先升级 Beacon。");
        }

        if (manifest.FormatVersion < CurrentFormatVersion)
        {
            throw new StateBackupValidationException("此状态备份格式已不受当前版本支持。");
        }

        if (manifest.BackupId == Guid.Empty ||
            manifest.CreatedAtUtc == default ||
            string.IsNullOrWhiteSpace(manifest.BeaconVersion) ||
            string.IsNullOrWhiteSpace(manifest.SourceClientId) ||
            !Guid.TryParse(manifest.SourceClientId, out var sourceClientId) ||
            sourceClientId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.Platform) ||
            string.IsNullOrWhiteSpace(manifest.Architecture) ||
            manifest.Encrypted)
        {
            throw new StateBackupValidationException("状态备份清单缺少必要信息。");
        }

        if (!Enum.TryParse<LocalStateBackupKind>(
                manifest.BackupKind,
                ignoreCase: true,
                out var backupKind) ||
            !Enum.IsDefined(backupKind) ||
            manifest.Databases is null ||
            manifest.Databases.Count != 2)
        {
            throw new StateBackupValidationException("状态备份清单内容无效。");
        }

        var databases = new Dictionary<string, StateBundleDatabaseManifest>(
            StringComparer.Ordinal);
        foreach (var database in manifest.Databases)
        {
            if (database is null ||
                string.IsNullOrWhiteSpace(database.Role) ||
                !databases.TryAdd(database.Role, database))
            {
                throw new StateBackupValidationException("状态备份包含重复的数据库角色。");
            }
        }

        ValidateDatabaseManifest(
            databases,
            "asset",
            AssetDatabaseEntryName,
            DatabaseMigrator.CurrentSchemaVersion);
        ValidateDatabaseManifest(
            databases,
            "reader",
            ReaderDatabaseEntryName,
            Reader.ReaderDatabaseMigrator.CurrentSchemaVersion);
    }

    private static void ValidateDatabaseManifest(
        IReadOnlyDictionary<string, StateBundleDatabaseManifest> databases,
        string role,
        string expectedPath,
        int supportedSchemaVersion)
    {
        if (!databases.TryGetValue(role, out var database) ||
            !database.Required ||
            !string.Equals(database.Path, expectedPath, StringComparison.Ordinal) ||
            database.Size <= 0 ||
            database.Size > MaximumDatabaseBytes ||
            database.SchemaVersion <= 0 ||
            string.IsNullOrWhiteSpace(database.Sha256) ||
            database.Sha256.Length != 64)
        {
            throw new StateBackupValidationException(
                "状态备份数据库清单不完整或无效。");
        }

        try
        {
            _ = Convert.FromHexString(database.Sha256);
        }
        catch (FormatException exception)
        {
            throw new StateBackupValidationException(
                "状态备份数据库校验值无效。",
                exception);
        }

        if (database.SchemaVersion > supportedSchemaVersion)
        {
            throw new StateBackupNewerVersionException(
                "此状态备份由更高版本的 Beacon 创建，请先升级 Beacon。");
        }
    }

    private static async Task ExtractDatabaseAsync(
        ZipArchiveEntry entry,
        StateBundleDatabaseManifest manifest,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (entry.Length != manifest.Size)
        {
            throw new StateBackupValidationException("状态备份数据库大小校验失败。");
        }

        try
        {
            await using (var source = entry.Open())
            await using (var destination = new FileStream(
                             destinationPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                _ = await CopyBoundedAsync(
                    source,
                    destination,
                    manifest.Size,
                    MaximumDatabaseBytes,
                    cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            if (new FileInfo(destinationPath).Length != manifest.Size)
            {
                throw new StateBackupValidationException("状态备份数据库解压大小不一致。");
            }

            var actualSha256 = await ComputeSha256Async(destinationPath, cancellationToken);
            if (!FixedTimeEquals(manifest.Sha256, actualSha256))
            {
                throw new StateBackupValidationException(
                    "状态备份校验失败，文件可能已损坏或被修改。");
            }

            await ValidateSqliteDatabaseAsync(
                destinationPath,
                manifest.Role,
                manifest.SchemaVersion,
                cancellationToken);
        }
        catch
        {
            TryDeleteFile(destinationPath);
            throw;
        }
    }

    internal static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long expectedLength,
        long maximumLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (expectedLength < 0 || maximumLength < 0 || expectedLength > maximumLength)
        {
            throw new StateBackupValidationException("状态备份条目大小无效。");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var remainingBeforeLimit = expectedLength - total;
                var readSize = (int)Math.Min(
                    buffer.Length,
                    remainingBeforeLimit + 1);
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, readSize),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (read > remainingBeforeLimit)
                {
                    throw new StateBackupValidationException(
                        "状态备份条目的实际内容超过清单声明的大小。");
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        if (total != expectedLength)
        {
            throw new StateBackupValidationException(
                "状态备份条目的实际大小与清单不一致。");
        }

        return total;
    }

    internal static async Task<int> ValidateSqliteDatabaseAsync(
        string path,
        string role,
        int? expectedSchemaVersion,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 10
        }.ToString();
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                await using var reader = await integrity.ExecuteReaderAsync(cancellationToken);
                var sawResult = false;
                while (await reader.ReadAsync(cancellationToken))
                {
                    sawResult = true;
                    var result = reader.GetString(0);
                    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new StateBackupValidationException(
                            $"SQLite 完整性检查失败：{result}");
                    }
                }

                if (!sawResult)
                {
                    throw new StateBackupValidationException("SQLite 完整性检查没有返回结果。");
                }
            }

            await using (var foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_key_check;";
                await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new StateBackupValidationException("SQLite 数据库存在外键约束错误。");
                }
            }

            var migrationTable = string.Equals(role, "asset", StringComparison.Ordinal)
                ? "schema_migrations"
                : string.Equals(role, "reader", StringComparison.Ordinal)
                    ? "reader_schema_migrations"
                    : throw new StateBackupValidationException("状态备份数据库角色无效。");
            await using var schema = connection.CreateCommand();
            schema.CommandText = $"SELECT COALESCE(MAX(version), 0) FROM {migrationTable};";
            var schemaVersion = ReadSqliteInt64(
                await schema.ExecuteScalarAsync(cancellationToken),
                "状态备份数据库的架构版本格式无效。");
            if (schemaVersion <= 0)
            {
                throw new StateBackupValidationException("状态备份数据库没有有效的架构版本。");
            }

            var supportedVersion = string.Equals(role, "asset", StringComparison.Ordinal)
                ? DatabaseMigrator.CurrentSchemaVersion
                : Reader.ReaderDatabaseMigrator.CurrentSchemaVersion;
            if (schemaVersion > supportedVersion)
            {
                throw new StateBackupNewerVersionException(
                    "此状态备份由更高版本的 Beacon 创建，请先升级 Beacon。");
            }

            await ValidateMigrationHistoryAsync(
                connection,
                migrationTable,
                schemaVersion,
                cancellationToken);

            if (expectedSchemaVersion is not null &&
                schemaVersion != expectedSchemaVersion.Value)
            {
                throw new StateBackupValidationException("状态备份记录的架构版本与数据库不一致。");
            }

            if (schemaVersion == supportedVersion)
            {
                await ValidateCurrentSchemaContractAsync(
                    connection,
                    role,
                    string.Equals(role, "asset", StringComparison.Ordinal)
                        ? AssetSchemaContract
                        : ReaderSchemaContract,
                    cancellationToken);
            }

            return checked((int)schemaVersion);
        }
        catch (StateBackupValidationException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new StateBackupValidationException(
                $"无法验证状态数据库：{Path.GetFileName(path)}。",
                exception);
        }
        catch (Exception exception) when (exception is InvalidCastException or
                                          OverflowException or
                                          FormatException)
        {
            throw new StateBackupValidationException(
                $"状态数据库包含格式无效的架构信息：{Path.GetFileName(path)}。",
                exception);
        }
    }

    private static async Task ValidateMigrationHistoryAsync(
        SqliteConnection connection,
        string migrationTable,
        long schemaVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT version FROM {migrationTable} ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        long expected = 1;
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = ReadSqliteInt64(
                reader.GetValue(0),
                "状态备份数据库的迁移记录格式无效。");
            if (version != expected)
            {
                throw new StateBackupValidationException(
                    "状态备份数据库的迁移记录不连续。");
            }

            expected++;
        }

        if (expected - 1 != schemaVersion)
        {
            throw new StateBackupValidationException(
                "状态备份数据库的迁移记录与架构版本不一致。");
        }
    }

    private static long ReadSqliteInt64(object? value, string message)
    {
        return value switch
        {
            long number => number,
            int number => number,
            short number => number,
            sbyte number => number,
            byte number => number,
            ushort number => number,
            uint number => number,
            _ => throw new StateBackupValidationException(message)
        };
    }

    private static async Task ValidateCurrentSchemaContractAsync(
        SqliteConnection connection,
        string role,
        IReadOnlyDictionary<string, string[]> contract,
        CancellationToken cancellationToken)
    {
        var runtimeSchemaContract = await GetRuntimeSchemaContractAsync(role);
        await ValidateUnsupportedSchemaObjectsAsync(connection, cancellationToken);
        var actualTables = await ReadSchemaTablesAsync(connection, cancellationToken);
        if (actualTables.Count != runtimeSchemaContract.Tables.Count ||
            runtimeSchemaContract.Tables.Any(expected =>
                !actualTables.TryGetValue(expected.Key, out var actual) ||
                !actual.Equals(expected.Value)))
        {
            throw new StateBackupValidationException(
                "状态备份数据库的表集合或表属性与当前版本不一致。");
        }

        foreach (var (tableName, requiredColumns) in contract)
        {
            await using (var table = connection.CreateCommand())
            {
                table.CommandText =
                    "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;";
                table.Parameters.AddWithValue("$name", tableName);
                var count = Convert.ToInt32(
                    await table.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
                if (count != 1)
                {
                    throw new StateBackupValidationException(
                        $"状态备份数据库缺少必要表：{tableName}。");
                }
            }

            await using var columns = connection.CreateCommand();
            columns.CommandText =
                "SELECT name, type, \"notnull\", dflt_value, pk, hidden " +
                "FROM pragma_table_xinfo($table_name);";
            columns.Parameters.AddWithValue("$table_name", tableName);
            await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
            var actualColumns = new Dictionary<string, SchemaColumn>(StringComparer.Ordinal);
            var actualPrimaryKey = new SortedDictionary<int, string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var columnName = reader.GetString(0);
                var primaryKeyOrdinal = reader.GetInt32(4);
                if (!actualColumns.TryAdd(
                        columnName,
                        new SchemaColumn(
                            columnName,
                            NormalizeDeclaredType(reader.GetString(1)),
                            reader.GetInt32(2) != 0,
                            reader.IsDBNull(3)
                                ? null
                                : NormalizeDefaultValue(reader.GetString(3)),
                            primaryKeyOrdinal,
                            reader.GetInt32(5))))
                {
                    throw new StateBackupValidationException(
                        $"状态备份数据库表 {tableName} 包含重复列定义。");
                }

                if (primaryKeyOrdinal > 0 &&
                    !actualPrimaryKey.TryAdd(primaryKeyOrdinal, columnName))
                {
                    throw new StateBackupValidationException(
                        $"状态备份数据库表 {tableName} 的主键定义无效。");
                }
            }

            var missing = requiredColumns
                .Where(column => !actualColumns.ContainsKey(column))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库表 {tableName} 缺少必要列：{string.Join(", ", missing)}。");
            }

            if (!runtimeSchemaContract.Columns.TryGetValue(tableName, out var expectedColumns) ||
                !expectedColumns.Keys.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(requiredColumns))
            {
                throw new StateBackupValidationException(
                    $"Beacon 内部数据库契约与迁移定义不一致：{tableName}。");
            }

            if (!actualColumns.Keys.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(expectedColumns.Keys))
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库表 {tableName} 包含当前版本不支持的额外列。");
            }

            foreach (var columnName in requiredColumns)
            {
                if (!actualColumns[columnName].Equals(expectedColumns[columnName]))
                {
                    throw new StateBackupValidationException(
                        $"状态备份数据库表 {tableName} 的列定义无效：{columnName}。");
                }
            }

            if (!PrimaryKeyContract.TryGetValue(tableName, out var expectedPrimaryKey) ||
                !actualPrimaryKey.Values.SequenceEqual(
                    expectedPrimaryKey,
                    StringComparer.Ordinal))
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库表 {tableName} 的主键约束无效。");
            }

            await ValidateUniqueKeysAsync(
                connection,
                tableName,
                cancellationToken);
            await ValidateForeignKeysAsync(
                connection,
                tableName,
                cancellationToken);
            await ValidateCheckConstraintsAsync(
                connection,
                tableName,
                cancellationToken);
            if (!runtimeSchemaContract.UserIndexes.TryGetValue(
                    tableName,
                    out var expectedUserIndexes))
            {
                throw new StateBackupValidationException(
                    $"Beacon 内部索引契约与迁移定义不一致：{tableName}。");
            }

            await ValidateUserCreatedIndexesAsync(
                connection,
                tableName,
                expectedUserIndexes,
                cancellationToken);
        }
    }

    private static async Task ValidateUnsupportedSchemaObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT type, name FROM sqlite_schema " +
            "WHERE type IN ('trigger', 'view') LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new StateBackupValidationException(
                $"状态备份数据库包含当前版本不支持的 {reader.GetString(0)}：" +
                $"{reader.GetString(1)}。");
        }
    }

    private static Task<RuntimeSchemaContract> GetRuntimeSchemaContractAsync(string role) =>
        string.Equals(role, "asset", StringComparison.Ordinal)
            ? AssetRuntimeSchemaContract.Value
            : string.Equals(role, "reader", StringComparison.Ordinal)
                ? ReaderRuntimeSchemaContract.Value
                : throw new StateBackupValidationException("状态备份数据库角色无效。");

    private static async Task<RuntimeSchemaContract> CreateRuntimeSchemaContractAsync(string role)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"beacon-schema-contract-{role}-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(CancellationToken.None);
        if (string.Equals(role, "asset", StringComparison.Ordinal))
        {
            await DatabaseMigrator.MigrateAsync(connectionString, CancellationToken.None);
        }
        else if (string.Equals(role, "reader", StringComparison.Ordinal))
        {
            await Reader.ReaderDatabaseMigrator.MigrateAsync(
                connectionString,
                CancellationToken.None);
        }
        else
        {
            throw new StateBackupValidationException("状态备份数据库角色无效。");
        }

        var tableContract = string.Equals(role, "asset", StringComparison.Ordinal)
            ? AssetSchemaContract
            : ReaderSchemaContract;
        var tables = await ReadSchemaTablesAsync(anchor, CancellationToken.None);
        var columnsByTable = new Dictionary<string, IReadOnlyDictionary<string, SchemaColumn>>(
            StringComparer.Ordinal);
        foreach (var tableName in tableContract.Keys)
        {
            await using var command = anchor.CreateCommand();
            command.CommandText =
                "SELECT name, type, \"notnull\", dflt_value, pk, hidden " +
                "FROM pragma_table_xinfo($table_name);";
            command.Parameters.AddWithValue("$table_name", tableName);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            var columns = new Dictionary<string, SchemaColumn>(StringComparer.Ordinal);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                var name = reader.GetString(0);
                columns.Add(
                    name,
                    new SchemaColumn(
                        name,
                        NormalizeDeclaredType(reader.GetString(1)),
                        reader.GetInt32(2) != 0,
                        reader.IsDBNull(3)
                            ? null
                            : NormalizeDefaultValue(reader.GetString(3)),
                        reader.GetInt32(4),
                        reader.GetInt32(5)));
            }

            columnsByTable.Add(tableName, columns);
        }

        var indexesByTable = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal);
        foreach (var tableName in tableContract.Keys)
        {
            await using var command = anchor.CreateCommand();
            command.CommandText =
                "SELECT name, sql FROM sqlite_schema " +
                "WHERE type = 'index' AND tbl_name = $table_name AND sql IS NOT NULL;";
            command.Parameters.AddWithValue("$table_name", tableName);
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            var indexes = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                indexes.Add(reader.GetString(0), NormalizeSql(reader.GetString(1)));
            }

            indexesByTable.Add(tableName, indexes);
        }

        return new RuntimeSchemaContract(tables, columnsByTable, indexesByTable);
    }

    private static async Task<IReadOnlyDictionary<string, SchemaTable>> ReadSchemaTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT tables.name, tables.type, tables.ncol, tables.wr, " +
            "tables.strict, schema_objects.sql " +
            "FROM pragma_table_list AS tables " +
            "LEFT JOIN sqlite_schema AS schema_objects " +
            "ON schema_objects.type = 'table' AND schema_objects.name = tables.name " +
            "WHERE tables.\"schema\" = 'main';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new Dictionary<string, SchemaTable>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (!tables.TryAdd(
                    name,
                    new SchemaTable(
                        name,
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3) != 0,
                        reader.GetInt32(4) != 0,
                        reader.IsDBNull(5)
                            ? null
                            : NormalizeSql(reader.GetString(5)))))
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库包含重复表定义：{name}。");
            }
        }

        return tables;
    }

    private static string NormalizeDeclaredType(string value) =>
        value.Trim().ToUpperInvariant();

    private static string NormalizeDefaultValue(string value) =>
        value.Trim();

    private static async Task ValidateUniqueKeysAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var expectedKeys = UniqueKeyContract.TryGetValue(tableName, out var configuredKeys)
            ? configuredKeys
            : [];

        var indexesToInspect = new List<(string Name, bool IsPartial)>();
        await using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText =
                "SELECT name, partial FROM pragma_index_list($table_name) " +
                "WHERE \"unique\" = 1;";
            indexes.Parameters.AddWithValue("$table_name", tableName);
            await using var reader = await indexes.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    indexesToInspect.Add((reader.GetString(0), reader.GetInt32(1) != 0));
                }
            }
        }

        var actualKeys = new List<SchemaUniqueKey>();
        foreach (var (indexName, isPartial) in indexesToInspect)
        {
            var keyColumns = new List<string>();
            var keyCollations = new List<string>();
            await using (var columns = connection.CreateCommand())
            {
                columns.CommandText =
                    "SELECT name, coll, key FROM pragma_index_xinfo($index_name) " +
                    "ORDER BY seqno;";
                columns.Parameters.AddWithValue("$index_name", indexName);
                await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0) &&
                        reader.GetInt32(2) != 0)
                    {
                        keyColumns.Add(reader.GetString(0));
                        keyCollations.Add(reader.IsDBNull(1)
                            ? string.Empty
                            : reader.GetString(1).ToUpperInvariant());
                    }
                }
            }

            if (keyColumns.Count > 0)
            {
                string? normalizedSql = null;
                if (isPartial)
                {
                    await using var sql = connection.CreateCommand();
                    sql.CommandText =
                        "SELECT sql FROM sqlite_schema WHERE type = 'index' AND name = $name;";
                    sql.Parameters.AddWithValue("$name", indexName);
                    normalizedSql = NormalizeSql(
                        Convert.ToString(
                            await sql.ExecuteScalarAsync(cancellationToken),
                            CultureInfo.InvariantCulture) ?? string.Empty);
                }

                actualKeys.Add(new SchemaUniqueKey(
                    CreateColumnSignature(keyColumns),
                    CreateColumnSignature(keyCollations),
                    isPartial,
                    normalizedSql));
            }
        }

        foreach (var expectedKey in expectedKeys)
        {
            if (!actualKeys.Any(actual => MatchesExpectedUniqueKey(
                    actual,
                    tableName,
                    expectedKey)))
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库表 {tableName} 缺少必要唯一约束：" +
                    $"{string.Join(", ", expectedKey)}。");
            }
        }

        var primaryKey = PrimaryKeyContract[tableName];
        foreach (var actual in actualKeys)
        {
            var matchesDeclaredUnique = expectedKeys.Any(expected =>
                MatchesExpectedUniqueKey(actual, tableName, expected));
            var matchesPrimaryKey = !actual.IsPartial &&
                string.Equals(
                    actual.ColumnSignature,
                    CreateColumnSignature(primaryKey),
                    StringComparison.Ordinal) &&
                string.Equals(
                    actual.CollationSignature,
                    CreateColumnSignature(
                        GetExpectedUniqueKeyCollations(tableName, primaryKey)),
                    StringComparison.Ordinal);
            if (!matchesDeclaredUnique && !matchesPrimaryKey)
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库表 {tableName} 包含当前版本不支持的唯一约束：" +
                    $"{actual.ColumnSignature.Replace('\u001f', ',')} " +
                    $"({actual.CollationSignature.Replace('\u001f', ',')})。");
            }
        }
    }

    private static async Task ValidateForeignKeysAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var expectedKeys = ForeignKeyContract.TryGetValue(tableName, out var configuredKeys)
            ? configuredKeys
            : [];

        var actualKeys = new HashSet<SchemaForeignKey>();
        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText =
            """
            SELECT "from", "table", "to", on_delete, on_update
            FROM pragma_foreign_key_list($table_name);
            """;
        foreignKeys.Parameters.AddWithValue("$table_name", tableName);
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) ||
                reader.IsDBNull(3) || reader.IsDBNull(4))
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库表 {tableName} 的外键定义无效。");
            }

            actualKeys.Add(new SchemaForeignKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        foreach (var expectedKey in expectedKeys)
        {
            if (!actualKeys.Contains(expectedKey))
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库表 {tableName} 缺少必要外键约束：" +
                    $"{expectedKey.FromColumn} -> {expectedKey.TargetTable}.{expectedKey.TargetColumn}。");
            }
        }

        if (!actualKeys.SetEquals(expectedKeys))
        {
            throw new StateBackupValidationException(
                $"状态备份数据库表 {tableName} 包含当前版本不支持的外键约束。");
        }
    }

    private static async Task ValidateCheckConstraintsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var expectedChecks = CheckConstraintContract.TryGetValue(
            tableName,
            out var configuredChecks)
            ? configuredChecks
            : [];

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        var sql = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) ?? string.Empty;
        var actualChecks = ExtractNormalizedCheckConstraints(sql);
        foreach (var expectedCheck in expectedChecks)
        {
            if (!actualChecks.Contains(expectedCheck))
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库表 {tableName} 缺少必要检查约束：{expectedCheck}。");
            }
        }


        if (!actualChecks.SetEquals(expectedChecks))
        {
            throw new StateBackupValidationException(
                $"状态备份数据库表 {tableName} 包含当前版本不支持的检查约束。");
        }
    }

    private static async Task ValidateUserCreatedIndexesAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlyDictionary<string, string> expectedIndexes,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, sql FROM sqlite_schema " +
            "WHERE type = 'index' AND tbl_name = $table_name AND sql IS NOT NULL;";
        command.Parameters.AddWithValue("$table_name", tableName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var actualIndexes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) ||
                !actualIndexes.TryAdd(
                    reader.GetString(0),
                    NormalizeSql(reader.GetString(1))))
            {
                throw new StateBackupValidationException(
                    $"状态备份数据库表 {tableName} 的索引定义无效。");
            }
        }

        if (actualIndexes.Count != expectedIndexes.Count ||
            expectedIndexes.Any(expected =>
                !actualIndexes.TryGetValue(expected.Key, out var actualSql) ||
                !string.Equals(actualSql, expected.Value, StringComparison.Ordinal)))
        {
            throw new StateBackupValidationException(
                $"状态备份数据库表 {tableName} 的用户索引与当前版本不一致。");
        }
    }

    private static string CreateColumnSignature(IEnumerable<string> columns) =>
        string.Join('\u001f', columns);

    private static bool IsDefaultSelectionUniqueKey(
        string tableName,
        IReadOnlyList<string> columns) =>
        columns.Count == 1 &&
        string.Equals(columns[0], "is_default", StringComparison.Ordinal) &&
        (string.Equals(tableName, "openweb_sources", StringComparison.Ordinal) ||
            string.Equals(tableName, "git_profiles", StringComparison.Ordinal));

    private static bool MatchesExpectedUniqueKey(
        SchemaUniqueKey actual,
        string tableName,
        IReadOnlyList<string> expectedColumns)
    {
        var expectsDefaultPredicate = IsDefaultSelectionUniqueKey(
            tableName,
            expectedColumns);
        return string.Equals(
                actual.ColumnSignature,
                CreateColumnSignature(expectedColumns),
                StringComparison.Ordinal) &&
            string.Equals(
                actual.CollationSignature,
                CreateColumnSignature(
                    GetExpectedUniqueKeyCollations(tableName, expectedColumns)),
                StringComparison.Ordinal) &&
            actual.IsPartial == expectsDefaultPredicate &&
            (!expectsDefaultPredicate ||
                actual.NormalizedSql?.EndsWith(
                    "WHEREIS_DEFAULT=1",
                    StringComparison.Ordinal) == true);
    }

    private static IEnumerable<string> GetExpectedUniqueKeyCollations(
        string tableName,
        IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            yield return (tableName, column) switch
            {
                ("local_volumes", "stable_id") => "NOCASE",
                ("asset_collections", "name") => "NOCASE",
                ("asset_tags", "normalized_name") => "NOCASE",
                ("openweb_sources", "origin_domain") => "NOCASE",
                ("openweb_publications", "origin_domain") => "NOCASE",
                ("git_profiles", "repository_url") => "NOCASE",
                _ => "BINARY"
            };
        }
    }

    private static string NormalizeSql(string value)
    {
        var text = RemoveSqlComments(value);
        var result = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (TryAppendQuotedToken(text, result, ref index))
            {
                continue;
            }

            if (!char.IsWhiteSpace(text[index]))
            {
                result.Append(char.ToUpperInvariant(text[index]));
            }
        }

        return result.ToString().TrimEnd(';');
    }

    private static HashSet<string> ExtractNormalizedCheckConstraints(string sql)
    {
        var text = RemoveSqlComments(sql);
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < text.Length; index++)
        {
            if (TrySkipQuotedToken(text, ref index))
            {
                continue;
            }

            if (!IsKeywordAt(text, index, "CHECK"))
            {
                continue;
            }

            var openingParenthesis = index + "CHECK".Length;
            while (openingParenthesis < text.Length &&
                   char.IsWhiteSpace(text[openingParenthesis]))
            {
                openingParenthesis++;
            }

            if (openingParenthesis >= text.Length ||
                text[openingParenthesis] != '(')
            {
                continue;
            }

            var depth = 0;
            var end = openingParenthesis;
            for (; end < text.Length; end++)
            {
                if (TrySkipQuotedToken(text, ref end))
                {
                    continue;
                }

                if (text[end] == '(')
                {
                    depth++;
                }
                else if (text[end] == ')' && --depth == 0)
                {
                    result.Add(NormalizeSql(text[index..(end + 1)]));
                    index = end;
                    break;
                }
            }
        }

        return result;
    }

    private static bool IsKeywordAt(string text, int index, string keyword)
    {
        if (index + keyword.Length > text.Length ||
            !text.AsSpan(index, keyword.Length).Equals(
                keyword,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var beforeIsIdentifier = index > 0 && IsIdentifierCharacter(text[index - 1]);
        var after = index + keyword.Length;
        var afterIsIdentifier = after < text.Length && IsIdentifierCharacter(text[after]);
        return !beforeIsIdentifier && !afterIsIdentifier;
    }

    private static bool IsIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    private static bool TrySkipQuotedToken(string text, ref int index)
    {
        var opening = text[index];
        var closing = opening == '[' ? ']' : opening;
        if (opening is not ('\'' or '"' or '`' or '['))
        {
            return false;
        }

        for (index++; index < text.Length; index++)
        {
            if (text[index] != closing)
            {
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == closing)
            {
                index++;
                continue;
            }

            return true;
        }

        return true;
    }

    private static string RemoveSqlComments(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (TryAppendQuotedToken(value, result, ref index))
            {
                continue;
            }

            if (value[index] == '-' &&
                index + 1 < value.Length &&
                value[index + 1] == '-')
            {
                index += 2;
                while (index < value.Length && value[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                result.Append(' ');
                continue;
            }

            if (value[index] == '/' &&
                index + 1 < value.Length &&
                value[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < value.Length &&
                       (value[index] != '*' || value[index + 1] != '/'))
                {
                    index++;
                }

                if (index + 1 < value.Length)
                {
                    index++;
                }

                result.Append(' ');
                continue;
            }

            result.Append(value[index]);
        }

        return result.ToString();
    }

    private static bool TryAppendQuotedToken(
        string text,
        StringBuilder destination,
        ref int index)
    {
        var opening = text[index];
        var closing = opening == '[' ? ']' : opening;
        if (opening is not ('\'' or '"' or '`' or '['))
        {
            return false;
        }

        destination.Append(opening);
        for (index++; index < text.Length; index++)
        {
            destination.Append(text[index]);
            if (text[index] != closing)
            {
                continue;
            }

            if (index + 1 < text.Length && text[index + 1] == closing)
            {
                destination.Append(text[++index]);
                continue;
            }

            return true;
        }

        return true;
    }

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string entryName,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.UtcNow;
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task WriteFileEntryAsync(
        ZipArchive archive,
        string entryName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.UtcNow;
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken);
    }

    private static bool FixedTimeEquals(string expectedHex, string actualHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHex),
                Convert.FromHexString(actualHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
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

    private sealed record SchemaColumn(
        string Name,
        string DeclaredType,
        bool NotNull,
        string? DefaultValue,
        int PrimaryKeyOrdinal,
        int Hidden);

    private sealed record SchemaTable(
        string Name,
        string Type,
        int ColumnCount,
        bool WithoutRowId,
        bool Strict,
        string? DefinitionSql);

    private sealed record RuntimeSchemaContract(
        IReadOnlyDictionary<string, SchemaTable> Tables,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, SchemaColumn>> Columns,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> UserIndexes);

    private sealed record SchemaUniqueKey(
        string ColumnSignature,
        string CollationSignature,
        bool IsPartial,
        string? NormalizedSql);

    private sealed record SchemaForeignKey(
        string FromColumn,
        string TargetTable,
        string TargetColumn,
        string OnDelete,
        string OnUpdate = "NO ACTION");
}
