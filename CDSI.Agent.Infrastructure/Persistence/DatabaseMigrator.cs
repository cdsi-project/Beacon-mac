using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

internal static class DatabaseMigrator
{
    internal const int CurrentSchemaVersion = 28;

    public static async Task MigrateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """,
            cancellationToken);

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        var currentVersion = Convert.ToInt64(
            await versionCommand.ExecuteScalarAsync(cancellationToken));
        if (currentVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"资产数据库架构版本 {currentVersion} 高于当前 Beacon 支持的版本 " +
                $"{CurrentSchemaVersion}，请升级 Beacon 后重试。");
        }

        if (currentVersion < 1)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
            CREATE TABLE devices (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                platform TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE scan_roots (
                id TEXT NOT NULL PRIMARY KEY,
                path TEXT NOT NULL,
                path_key TEXT NOT NULL UNIQUE,
                enabled INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                last_scanned_at TEXT NULL
            );

            CREATE TABLE scan_jobs (
                id TEXT NOT NULL PRIMARY KEY,
                scan_root_id TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT NULL,
                files_discovered INTEGER NOT NULL,
                files_processed INTEGER NOT NULL,
                errors INTEGER NOT NULL,
                error_message TEXT NULL,
                FOREIGN KEY (scan_root_id) REFERENCES scan_roots(id)
            );

            CREATE TABLE assets (
                id TEXT NOT NULL PRIMARY KEY,
                original_filename TEXT NOT NULL,
                mime_type TEXT NULL,
                extension TEXT NOT NULL,
                size INTEGER NOT NULL,
                sha256 TEXT NULL,
                created_at TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                discovered_at TEXT NOT NULL,
                status TEXT NOT NULL
            );

            CREATE TABLE asset_locations (
                id TEXT NOT NULL PRIMARY KEY,
                asset_id TEXT NOT NULL,
                location_type TEXT NOT NULL,
                device_id TEXT NOT NULL,
                path TEXT NOT NULL,
                path_key TEXT NOT NULL,
                status TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                last_verified_at TEXT NULL,
                FOREIGN KEY (asset_id) REFERENCES assets(id),
                FOREIGN KEY (device_id) REFERENCES devices(id),
                UNIQUE (device_id, path_key)
            );

            CREATE INDEX ix_assets_sha256 ON assets(sha256);
            CREATE INDEX ix_assets_discovered_at ON assets(discovered_at DESC);
            CREATE INDEX ix_asset_locations_asset_id ON asset_locations(asset_id);
            CREATE INDEX ix_scan_jobs_scan_root_id ON scan_jobs(scan_root_id);

            INSERT INTO schema_migrations(version, applied_at)
            VALUES (1, $applied_at);
            """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (currentVersion < 2)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE asset_metadata (
                    asset_id TEXT NOT NULL PRIMARY KEY,
                    extractor_name TEXT NOT NULL,
                    pipeline_version INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    source_size INTEGER NOT NULL,
                    source_modified_at TEXT NOT NULL,
                    metadata_json TEXT NULL,
                    error_message TEXT NULL,
                    extracted_at TEXT NOT NULL,
                    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE CASCADE
                );

                CREATE INDEX ix_asset_metadata_status
                ON asset_metadata(status);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (2, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (currentVersion < 3)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (3, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (currentVersion < 4)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                ALTER TABLE scan_roots
                    ADD COLUMN mode TEXT NOT NULL DEFAULT 'Readonly';
                ALTER TABLE scan_roots
                    ADD COLUMN status TEXT NOT NULL DEFAULT 'Active';
                ALTER TABLE scan_roots
                    ADD COLUMN updated_at TEXT NULL;
                ALTER TABLE scan_roots
                    ADD COLUMN removed_at TEXT NULL;

                UPDATE scan_roots
                SET updated_at = created_at
                WHERE updated_at IS NULL;

                CREATE TABLE managed_workspaces (
                    id TEXT NOT NULL PRIMARY KEY,
                    device_id TEXT NOT NULL UNIQUE,
                    path TEXT NOT NULL,
                    path_key TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY (device_id) REFERENCES devices(id)
                );

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (4, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (currentVersion < 5)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE storage_profiles (
                    id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    endpoint TEXT NOT NULL,
                    bucket_name TEXT NOT NULL,
                    region TEXT NULL,
                    use_https INTEGER NOT NULL,
                    access_key_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (5, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (currentVersion < 6)
        {
            var assetLocationsExist = await TableExistsAsync(
                connection,
                "asset_locations",
                cancellationToken);
            var ownershipColumnExists =
                await AssetLocationOwnershipColumnExistsAsync(
                    connection,
                    cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (assetLocationsExist && !ownershipColumnExists)
            {
                await using var locationMigrationCommand = connection.CreateCommand();
                locationMigrationCommand.Transaction = (SqliteTransaction)transaction;
                locationMigrationCommand.CommandText =
                    """
                    ALTER TABLE asset_locations
                        ADD COLUMN ownership TEXT NOT NULL DEFAULT 'External';
                    """;
                await locationMigrationCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE file_operations (
                    id TEXT NOT NULL PRIMARY KEY,
                    action TEXT NOT NULL,
                    status TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    total_items INTEGER NOT NULL,
                    completed_items INTEGER NOT NULL,
                    failed_items INTEGER NOT NULL,
                    error_message TEXT NULL
                );

                CREATE TABLE file_operation_items (
                    id TEXT NOT NULL PRIMARY KEY,
                    operation_id TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    source_path TEXT NOT NULL,
                    target_path TEXT NULL,
                    status TEXT NOT NULL,
                    source_deleted INTEGER NOT NULL,
                    sha256 TEXT NULL,
                    error_message TEXT NULL,
                    finished_at TEXT NULL,
                    FOREIGN KEY (operation_id) REFERENCES file_operations(id) ON DELETE CASCADE,
                    FOREIGN KEY (asset_id) REFERENCES assets(id)
                );

                CREATE INDEX ix_file_operation_items_operation_id
                ON file_operation_items(operation_id);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (6, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (currentVersion < 7)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE object_storage_locations (
                    id TEXT NOT NULL PRIMARY KEY,
                    asset_id TEXT NOT NULL,
                    storage_profile_id TEXT NOT NULL,
                    object_key TEXT NOT NULL,
                    status TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    sha256 TEXT NULL,
                    etag TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_verified_at TEXT NULL,
                    FOREIGN KEY (asset_id) REFERENCES assets(id),
                    UNIQUE (storage_profile_id, object_key)
                );

                CREATE TABLE upload_jobs (
                    id TEXT NOT NULL PRIMARY KEY,
                    storage_profile_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    total_items INTEGER NOT NULL,
                    completed_items INTEGER NOT NULL,
                    failed_items INTEGER NOT NULL,
                    total_bytes INTEGER NOT NULL,
                    uploaded_bytes INTEGER NOT NULL,
                    error_message TEXT NULL
                );

                CREATE TABLE upload_items (
                    id TEXT NOT NULL PRIMARY KEY,
                    job_id TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    source_path TEXT NOT NULL,
                    object_key TEXT NOT NULL,
                    status TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    uploaded_bytes INTEGER NOT NULL,
                    etag TEXT NULL,
                    error_message TEXT NULL,
                    finished_at TEXT NULL,
                    FOREIGN KEY (job_id) REFERENCES upload_jobs(id) ON DELETE CASCADE,
                    FOREIGN KEY (asset_id) REFERENCES assets(id)
                );

                CREATE TABLE multipart_upload_sessions (
                    storage_profile_id TEXT NOT NULL,
                    object_key TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    source_path TEXT NOT NULL,
                    upload_id TEXT NOT NULL,
                    part_size INTEGER NOT NULL,
                    source_size INTEGER NOT NULL,
                    source_modified_at TEXT NOT NULL,
                    parts_json TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (storage_profile_id, object_key),
                    FOREIGN KEY (asset_id) REFERENCES assets(id)
                );

                CREATE INDEX ix_object_storage_locations_asset_id
                ON object_storage_locations(asset_id);
                CREATE INDEX ix_upload_items_job_id
                ON upload_items(job_id);
                CREATE INDEX ix_multipart_upload_sessions_asset_id
                ON multipart_upload_sessions(asset_id);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (7, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (currentVersion < 8)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE asset_collections (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    type TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE asset_collection_items (
                    collection_id TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    added_at TEXT NOT NULL,
                    PRIMARY KEY (collection_id, asset_id),
                    FOREIGN KEY (collection_id)
                        REFERENCES asset_collections(id) ON DELETE CASCADE,
                    FOREIGN KEY (asset_id)
                        REFERENCES assets(id) ON DELETE CASCADE
                );

                CREATE INDEX ix_asset_collection_items_asset_id
                ON asset_collection_items(asset_id);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (8, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 9)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE agent_settings (
                    setting_key TEXT NOT NULL PRIMARY KEY,
                    setting_value TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (9, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 10)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE openweb_publications (
                    asset_id TEXT NOT NULL,
                    publisher TEXT NOT NULL,
                    origin_domain TEXT NOT NULL COLLATE NOCASE,
                    remote_post_id INTEGER NOT NULL,
                    remote_url TEXT NOT NULL,
                    remote_status TEXT NOT NULL,
                    content_sha256 TEXT NOT NULL,
                    synchronized_at TEXT NOT NULL,
                    PRIMARY KEY (asset_id, publisher, origin_domain),
                    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE CASCADE
                );

                CREATE INDEX ix_openweb_publications_remote_post
                ON openweb_publications(publisher, origin_domain, remote_post_id);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (10, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 11)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE INDEX ix_asset_locations_type_asset_id
                ON asset_locations(location_type, asset_id);

                CREATE INDEX ix_assets_created_at_julian
                ON assets(julianday(created_at));

                CREATE INDEX ix_assets_mime_type
                ON assets(mime_type);

                CREATE INDEX ix_assets_extension_lower
                ON assets(lower(extension));

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (11, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 12)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE local_volumes (
                    id TEXT NOT NULL PRIMARY KEY,
                    stable_id TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    serial_number TEXT NOT NULL,
                    label TEXT NULL,
                    filesystem TEXT NULL,
                    drive_type TEXT NOT NULL,
                    mount_path TEXT NOT NULL,
                    mount_path_key TEXT NOT NULL,
                    is_online INTEGER NOT NULL,
                    first_seen_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                ALTER TABLE scan_roots
                    ADD COLUMN volume_id TEXT NULL;
                ALTER TABLE scan_roots
                    ADD COLUMN volume_relative_path TEXT NULL;

                ALTER TABLE managed_workspaces
                    ADD COLUMN volume_id TEXT NULL;
                ALTER TABLE managed_workspaces
                    ADD COLUMN volume_relative_path TEXT NULL;

                ALTER TABLE asset_locations
                    ADD COLUMN volume_id TEXT NULL;
                ALTER TABLE asset_locations
                    ADD COLUMN volume_relative_path TEXT NULL;

                CREATE INDEX ix_scan_roots_volume_id
                ON scan_roots(volume_id);

                CREATE INDEX ix_asset_locations_volume_id
                ON asset_locations(volume_id);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (12, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 13)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                ALTER TABLE scan_roots
                    ADD COLUMN file_type_filter TEXT NOT NULL DEFAULT 'All';

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (13, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 14)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                ALTER TABLE scan_roots
                    ADD COLUMN extension_whitelist_json TEXT NOT NULL DEFAULT '[]';

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (14, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 15)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                ALTER TABLE assets
                    ADD COLUMN hidden_from_asset_list INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE assets
                    ADD COLUMN hidden_from_asset_list_at TEXT NULL;

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (15, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 16)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE openweb_sources (
                    id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    origin_domain TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    wordpress_username TEXT NOT NULL,
                    is_default INTEGER NOT NULL CHECK(is_default IN (0, 1)),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE UNIQUE INDEX ux_openweb_sources_default
                ON openweb_sources(is_default)
                WHERE is_default = 1;

                INSERT INTO openweb_sources(
                    id, display_name, origin_domain, wordpress_username,
                    is_default, created_at, updated_at)
                SELECT
                    '00000000-0000-0000-0000-000000000001',
                    origin.setting_value,
                    origin.setting_value,
                    username.setting_value,
                    1,
                    CASE WHEN origin.updated_at < username.updated_at
                         THEN origin.updated_at ELSE username.updated_at END,
                    CASE WHEN origin.updated_at > username.updated_at
                         THEN origin.updated_at ELSE username.updated_at END
                FROM agent_settings AS origin
                JOIN agent_settings AS username
                  ON username.setting_key = 'openweb.wordpress_username'
                WHERE origin.setting_key = 'openweb.origin_domain'
                  AND trim(origin.setting_value) <> ''
                  AND trim(username.setting_value) <> '';

                DELETE FROM agent_settings
                WHERE setting_key IN (
                    'openweb.origin_domain',
                    'openweb.wordpress_username');

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (16, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 17)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE restore_jobs (
                    id TEXT NOT NULL PRIMARY KEY,
                    status TEXT NOT NULL,
                    destination_kind TEXT NOT NULL,
                    target_directory TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    finished_at TEXT NULL,
                    total_items INTEGER NOT NULL,
                    completed_items INTEGER NOT NULL,
                    failed_items INTEGER NOT NULL,
                    total_bytes INTEGER NOT NULL,
                    downloaded_bytes INTEGER NOT NULL,
                    error_message TEXT NULL
                );

                CREATE TABLE restore_items (
                    id TEXT NOT NULL PRIMARY KEY,
                    job_id TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    storage_profile_id TEXT NOT NULL,
                    object_key TEXT NOT NULL,
                    target_path TEXT NOT NULL,
                    status TEXT NOT NULL,
                    size INTEGER NOT NULL,
                    downloaded_bytes INTEGER NOT NULL,
                    sha256 TEXT NULL,
                    error_message TEXT NULL,
                    finished_at TEXT NULL,
                    FOREIGN KEY (job_id) REFERENCES restore_jobs(id) ON DELETE CASCADE,
                    FOREIGN KEY (asset_id) REFERENCES assets(id)
                );

                CREATE INDEX ix_restore_items_job_id
                ON restore_items(job_id);

                CREATE INDEX ix_restore_items_asset_id
                ON restore_items(asset_id);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (17, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 18)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE asset_tags (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    normalized_name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE asset_tag_links (
                    asset_id TEXT NOT NULL,
                    tag_id TEXT NOT NULL,
                    tagged_at TEXT NOT NULL,
                    PRIMARY KEY (asset_id, tag_id),
                    FOREIGN KEY (asset_id) REFERENCES assets(id) ON DELETE CASCADE,
                    FOREIGN KEY (tag_id) REFERENCES asset_tags(id) ON DELETE CASCADE
                );

                CREATE INDEX ix_asset_tag_links_tag_id
                ON asset_tag_links(tag_id);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (18, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 19)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                ALTER TABLE asset_locations
                ADD COLUMN excluded_from_asset_list INTEGER NOT NULL DEFAULT 0;

                ALTER TABLE asset_locations
                ADD COLUMN excluded_from_asset_list_at TEXT NULL;

                CREATE TABLE asset_directory_exclusions (
                    path_key TEXT NOT NULL PRIMARY KEY,
                    path TEXT NOT NULL,
                    path_prefix TEXT NOT NULL,
                    excluded_at TEXT NOT NULL
                );

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (19, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 20)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE asset_collection_deletion_audit (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    collection_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL,
                    asset_count INTEGER NOT NULL,
                    deleted_at TEXT NOT NULL
                );

                CREATE INDEX ix_asset_collection_deletion_audit_collection_id
                ON asset_collection_deletion_audit(collection_id);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (20, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 21)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
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

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (21, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 22)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                ALTER TABLE git_profiles
                    ADD COLUMN authentication_method TEXT NOT NULL DEFAULT 'Password';
                ALTER TABLE git_profiles
                    ADD COLUMN ssh_public_key_path TEXT NULL;

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (22, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 23)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                ALTER TABLE scan_roots
                    ADD COLUMN file_type_filters_json TEXT NOT NULL
                    DEFAULT '["Video","Audio","Image","Document","Other"]';

                UPDATE scan_roots
                SET file_type_filters_json = CASE
                    WHEN extension_whitelist_json <> '[]' THEN '[]'
                    WHEN file_type_filter = 'All'
                        THEN '["Video","Audio","Image","Document","Other"]'
                    ELSE '["' || file_type_filter || '"]'
                END;

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (23, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 24)
        {
            var hasAssetCollections = await TableExistsAsync(
                connection,
                "asset_collections",
                cancellationToken);
            var hasStorageProfiles = await TableExistsAsync(
                connection,
                "storage_profiles",
                cancellationToken);
            var hasBackupProfileColumn = hasAssetCollections &&
                await ColumnExistsAsync(
                    connection,
                    "asset_collections",
                    "backup_profile_id",
                    cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            if (hasAssetCollections && hasStorageProfiles && !hasBackupProfileColumn)
            {
                migrationCommand.CommandText =
                    """
                ALTER TABLE asset_collections
                    ADD COLUMN backup_profile_id TEXT NULL
                    REFERENCES storage_profiles(id) ON DELETE SET NULL;
                """;
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (hasAssetCollections && hasStorageProfiles)
            {
                migrationCommand.CommandText =
                    """
                CREATE INDEX IF NOT EXISTS ix_asset_collections_backup_profile_id
                ON asset_collections(backup_profile_id);
                """;
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            migrationCommand.CommandText =
                """
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (24, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 25)
        {
            var hasAssetCollections = await TableExistsAsync(
                connection,
                "asset_collections",
                cancellationToken);
            var hasStorageProfiles = await TableExistsAsync(
                connection,
                "storage_profiles",
                cancellationToken);
            var hasLegacyBackupProfileColumn = hasAssetCollections &&
                await ColumnExistsAsync(
                    connection,
                    "asset_collections",
                    "backup_profile_id",
                    cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            if (hasAssetCollections && hasStorageProfiles)
            {
                migrationCommand.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS asset_collection_backup_profiles (
                        collection_id TEXT NOT NULL,
                        profile_id TEXT NOT NULL,
                        added_at TEXT NOT NULL,
                        PRIMARY KEY (collection_id, profile_id),
                        FOREIGN KEY (collection_id)
                            REFERENCES asset_collections(id) ON DELETE CASCADE,
                        FOREIGN KEY (profile_id)
                            REFERENCES storage_profiles(id) ON DELETE CASCADE
                    );

                    CREATE INDEX IF NOT EXISTS ix_asset_collection_backup_profiles_profile_id
                    ON asset_collection_backup_profiles(profile_id);
                    """;
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken);

                if (hasLegacyBackupProfileColumn)
                {
                    migrationCommand.CommandText =
                        """
                        INSERT OR IGNORE INTO asset_collection_backup_profiles (
                            collection_id, profile_id, added_at)
                        SELECT id, backup_profile_id, updated_at
                        FROM asset_collections
                        WHERE backup_profile_id IS NOT NULL;
                        """;
                    await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            migrationCommand.CommandText =
                """
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (25, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 26)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                CREATE TABLE IF NOT EXISTS git_project_syncs (
                    project_id TEXT NOT NULL,
                    profile_id TEXT NOT NULL,
                    project_name TEXT NOT NULL,
                    project_type TEXT NOT NULL,
                    profile_name TEXT NOT NULL,
                    provider TEXT NOT NULL,
                    repository_url TEXT NOT NULL,
                    branch TEXT NOT NULL,
                    commit_id TEXT NOT NULL,
                    synced_files INTEGER NOT NULL CHECK(synced_files >= 0),
                    synced_bytes INTEGER NOT NULL CHECK(synced_bytes >= 0),
                    created_commit INTEGER NOT NULL CHECK(created_commit IN (0, 1)),
                    synced_at TEXT NOT NULL,
                    PRIMARY KEY (project_id, profile_id)
                );

                CREATE INDEX IF NOT EXISTS ix_git_project_syncs_synced_at
                ON git_project_syncs(synced_at DESC);

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (26, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 27)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                DROP TABLE IF EXISTS asset_text;

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (27, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (currentVersion < 28)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = (SqliteTransaction)transaction;
            migrationCommand.CommandText =
                """
                ALTER TABLE scan_roots
                ADD COLUMN idle_scan_enabled INTEGER NOT NULL DEFAULT 0;

                ALTER TABLE scan_roots
                ADD COLUMN idle_scan_interval INTEGER NOT NULL DEFAULT 1
                    CHECK (idle_scan_interval BETWEEN 1 AND 999);

                ALTER TABLE scan_roots
                ADD COLUMN idle_scan_unit TEXT NOT NULL DEFAULT 'Hours'
                    CHECK (idle_scan_unit IN ('Minutes', 'Hours', 'Days'));

                INSERT INTO schema_migrations(version, applied_at)
                VALUES (28, $applied_at);
                """;
            migrationCommand.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table' AND name = $table_name);
            """;
        command.Parameters.AddWithValue("$table_name", tableName);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task<bool> AssetLocationOwnershipColumnExistsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM pragma_table_info('asset_locations')
                WHERE name = 'ownership');
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM pragma_table_info($table_name)
                WHERE name = $column_name);
            """;
        command.Parameters.AddWithValue("$table_name", tableName);
        command.Parameters.AddWithValue("$column_name", columnName);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
