using System.Globalization;
using System.Text.Json;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Duplicates;
using CDSI.Agent.Core.Fingerprints;
using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository : IAssetRepository
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteAssetRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("Database path has no parent directory.");
        Directory.CreateDirectory(directory);
        await DatabaseMigrator.MigrateAsync(_connectionString, cancellationToken);
    }

    public async Task<string> GetOrCreateDeviceIdAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var findCommand = connection.CreateCommand();
        findCommand.CommandText = "SELECT id FROM devices ORDER BY created_at LIMIT 1;";

        var existingId = await findCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.IsNullOrWhiteSpace(existingId))
        {
            return existingId;
        }

        var deviceId = Guid.NewGuid().ToString("D");
        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            """
            INSERT INTO devices(id, name, platform, created_at)
            VALUES ($id, $name, $platform, $created_at);
            """;
        insertCommand.Parameters.AddWithValue("$id", deviceId);
        insertCommand.Parameters.AddWithValue("$name", Environment.MachineName);
        insertCommand.Parameters.AddWithValue("$platform", Environment.OSVersion.Platform.ToString());
        insertCommand.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);

        return deviceId;
    }

    public async Task<ScanRoot> GetOrCreateScanRootAsync(
        string path,
        ScanRootMode mode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        var pathKey = CreatePathKey(normalizedPath);
        var proposedId = Guid.NewGuid();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.CommandText =
                """
                INSERT INTO scan_roots(
                    id, path, path_key, mode, enabled, status,
                    created_at, updated_at, last_scanned_at, removed_at)
                VALUES (
                    $id, $path, $path_key, $mode, 1, 'Active',
                    $created_at, $updated_at, NULL, NULL)
                ON CONFLICT(path_key) DO UPDATE SET
                    path = excluded.path,
                    mode = excluded.mode,
                    enabled = 1,
                    status = CASE
                        WHEN scan_roots.volume_id IS NOT NULL AND EXISTS (
                            SELECT 1
                            FROM local_volumes v
                            WHERE v.id = scan_roots.volume_id
                              AND v.is_online = 0
                        ) THEN 'Offline'
                        ELSE 'Active'
                    END,
                    updated_at = excluded.updated_at,
                    removed_at = NULL;
                """;
            insertCommand.Parameters.AddWithValue("$id", proposedId.ToString("D"));
            insertCommand.Parameters.AddWithValue("$path", normalizedPath);
            insertCommand.Parameters.AddWithValue("$path_key", pathKey);
            insertCommand.Parameters.AddWithValue("$mode", mode.ToString());
            insertCommand.Parameters.AddWithValue("$created_at", now.ToString("O"));
            insertCommand.Parameters.AddWithValue("$updated_at", now.ToString("O"));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText =
            """
            SELECT
                id, path, mode, enabled, status, created_at,
                updated_at, last_scanned_at, removed_at,
                volume_id, volume_relative_path, file_type_filter,
                extension_whitelist_json, file_type_filters_json,
                idle_scan_enabled, idle_scan_interval, idle_scan_unit
            FROM scan_roots
            WHERE path_key = $path_key;
            """;
        selectCommand.Parameters.AddWithValue("$path_key", pathKey);

        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Unable to load the scan root after registration.");
        }

        return ReadScanRoot(reader);
    }
    public async Task CreateScanJobAsync(
        ScanJob job,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO scan_jobs(
                id, scan_root_id, status, started_at, finished_at,
                files_discovered, files_processed, errors, error_message)
            VALUES (
                $id, $scan_root_id, $status, $started_at, $finished_at,
                $files_discovered, $files_processed, $errors, $error_message);
            """;
        AddScanJobParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateScanJobAsync(
        ScanJob job,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_jobs
            SET status = $status,
                finished_at = $finished_at,
                files_discovered = $files_discovered,
                files_processed = $files_processed,
                errors = $errors,
                error_message = $error_message
            WHERE id = $id;
            """;
        AddScanJobParameters(command, job);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkScanRootCompletedAsync(
        Guid scanRootId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE scan_roots
            SET last_scanned_at = $last_scanned_at,
                status = 'Active',
                updated_at = $last_scanned_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", scanRootId.ToString("D"));
        command.Parameters.AddWithValue("$last_scanned_at", completedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkMissingLocalLocationsAsync(
        string deviceId,
        string rootPath,
        DateTimeOffset scanStartedAt,
        ScanFileFilter? fileFilter = null,
        CancellationToken cancellationToken = default)
    {
        fileFilter ??= new ScanFileFilter();

        var normalizedRoot = NormalizePath(rootPath);
        var rootKey = CreatePathKey(normalizedRoot);
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var fileTypeCondition = CreateScanFileFilterCondition(fileFilter);
        var fileTypeClause = fileTypeCondition is null
            ? string.Empty
            : $"""
              AND EXISTS (
                  SELECT 1
                  FROM assets a
                  WHERE a.id = asset_locations.asset_id
                    AND {fileTypeCondition}
              )
              """;
        command.CommandText =
            $"""
            UPDATE asset_locations
            SET status = 'Missing'
            WHERE device_id = $device_id
              AND location_type = 'Local'
              AND excluded_from_asset_list = 0
              AND last_seen_at < $scan_started_at
              AND (
                  path_key = $root_key
                  OR substr(path_key, 1, length($root_prefix)) = $root_prefix
              )
              {fileTypeClause};
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$scan_started_at", scanStartedAt.ToString("O"));
        command.Parameters.AddWithValue("$root_key", rootKey);
        command.Parameters.AddWithValue("$root_prefix", CreatePathKey(rootPrefix));
        for (var index = 0; index < fileFilter.ExtensionWhitelist.Count; index++)
        {
            command.Parameters.AddWithValue(
                $"$scan_extension_{index}",
                fileFilter.ExtensionWhitelist[index]);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RegisteredLocalAsset>> RegisterLocalFilesAsync(
        string deviceId,
        IReadOnlyCollection<DiscoveredFile> files,
        DateTimeOffset discoveredAt,
        CancellationToken cancellationToken = default)
    {
        if (files.Count == 0)
        {
            return [];
        }

        var registered = new List<RegisteredLocalAsset>(files.Count);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var volumeMounts = await LoadOnlineVolumeMountsAsync(
            connection,
            (SqliteTransaction)transaction,
            cancellationToken);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedPath = NormalizePath(file.FullPath);
            var pathKey = CreatePathKey(normalizedPath);
            var volumeBinding = FindVolumeBinding(volumeMounts, normalizedPath);
            var existingAsset = await FindAssetAsync(
                connection,
                (SqliteTransaction)transaction,
                deviceId,
                pathKey,
                cancellationToken);
            var assetId = existingAsset?.Id;
            var requiresFingerprint = existingAsset is null ||
                existingAsset.Size != file.Size ||
                existingAsset.ModifiedAt != file.ModifiedAt ||
                string.IsNullOrWhiteSpace(existingAsset.Sha256);

            if (assetId is null)
            {
                assetId = Guid.NewGuid();
                await InsertAssetAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    assetId.Value,
                    file,
                    discoveredAt,
                    cancellationToken);
                await InsertLocationAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    Guid.NewGuid(),
                    assetId.Value,
                    deviceId,
                    normalizedPath,
                    pathKey,
                    volumeBinding,
                    discoveredAt,
                    cancellationToken);
            }
            else
            {
                await UpdateAssetAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    assetId.Value,
                    file,
                    discoveredAt,
                    cancellationToken);
                await UpdateLocationAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    deviceId,
                    normalizedPath,
                    pathKey,
                    volumeBinding,
                    discoveredAt,
                    cancellationToken);
            }

            registered.Add(new RegisteredLocalAsset(
                assetId.Value,
                file with { FullPath = normalizedPath },
                requiresFingerprint));
        }

        await transaction.CommitAsync(cancellationToken);
        return registered;
    }

    public async Task<bool> SaveSha256Async(
        Guid assetId,
        long expectedSize,
        DateTimeOffset expectedModifiedAt,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64)
        {
            throw new ArgumentException("A SHA-256 value must contain 64 hexadecimal characters.", nameof(sha256));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE assets
            SET sha256 = $sha256
            WHERE id = $id
              AND size = $expected_size
              AND modified_at = $expected_modified_at;
            """;
        command.Parameters.AddWithValue("$sha256", sha256.ToLowerInvariant());
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        command.Parameters.AddWithValue("$expected_size", expectedSize);
        command.Parameters.AddWithValue(
            "$expected_modified_at",
            expectedModifiedAt.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<FingerprintWorkSummary> GetFingerprintWorkSummaryAsync(
        FingerprintMode mode,
        CancellationToken cancellationToken = default)
    {
        var modeFilter = GetFingerprintModeFilter(mode);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COUNT(*), COALESCE(SUM(a.size), 0)
            FROM assets a
            WHERE a.sha256 IS NULL
              AND EXISTS (
                  SELECT 1
                  FROM asset_locations l
                  WHERE l.asset_id = a.id
                    AND l.location_type = 'Local'
                    AND l.status = 'Available'
                    AND l.excluded_from_asset_list = 0
              )
              {modeFilter};
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new FingerprintWorkSummary(0, 0);
        }

        return new FingerprintWorkSummary(reader.GetInt32(0), reader.GetInt64(1));
    }

    public async Task<IReadOnlyList<FingerprintCandidate>> ListFingerprintCandidatesAsync(
        FingerprintMode mode,
        Guid? afterAssetId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var modeFilter = GetFingerprintModeFilter(mode);
        var candidates = new List<FingerprintCandidate>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                a.id,
                a.original_filename,
                a.extension,
                a.mime_type,
                a.size,
                a.created_at,
                a.modified_at,
                MIN(l.path)
            FROM assets a
            INNER JOIN asset_locations l ON l.asset_id = a.id
            WHERE a.sha256 IS NULL
              AND l.location_type = 'Local'
              AND l.status = 'Available'
              AND l.excluded_from_asset_list = 0
              AND ($after_asset_id IS NULL OR a.id > $after_asset_id)
              {modeFilter}
            GROUP BY
                a.id,
                a.original_filename,
                a.extension,
                a.mime_type,
                a.size,
                a.created_at,
                a.modified_at
            ORDER BY a.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue(
            "$after_asset_id",
            afterAssetId is null ? DBNull.Value : afterAssetId.Value.ToString("D"));
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new FingerprintCandidate(
                Guid.Parse(reader.GetString(0)),
                new DiscoveredFile(
                    reader.GetString(7),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4),
                    ParseTimestamp(reader.GetString(5)),
                    ParseTimestamp(reader.GetString(6)))));
        }

        return candidates;
    }

    public async Task<MetadataWorkSummary> GetMetadataWorkSummaryAsync(
        int pipelineVersion,
        CancellationToken cancellationToken = default)
    {
        if (pipelineVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pipelineVersion));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM assets a
            WHERE EXISTS (
                SELECT 1
                FROM asset_locations l
                  WHERE l.asset_id = a.id
                    AND l.location_type = 'Local'
                    AND l.status = 'Available'
                    AND l.excluded_from_asset_list = 0
            )
              AND NOT EXISTS (
                  SELECT 1
                  FROM asset_metadata m
                  WHERE m.asset_id = a.id
                    AND m.pipeline_version = $pipeline_version
                    AND m.source_size = a.size
                    AND m.source_modified_at = a.modified_at
              );
            """;
        command.Parameters.AddWithValue("$pipeline_version", pipelineVersion);

        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return new MetadataWorkSummary(count);
    }

    public async Task<IReadOnlyList<MetadataCandidate>> ListMetadataCandidatesAsync(
        int pipelineVersion,
        Guid? afterAssetId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (pipelineVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pipelineVersion));
        }

        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var candidates = new List<MetadataCandidate>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                a.id,
                a.original_filename,
                a.extension,
                a.mime_type,
                a.size,
                a.created_at,
                a.modified_at,
                MIN(l.path)
            FROM assets a
            INNER JOIN asset_locations l ON l.asset_id = a.id
            WHERE l.location_type = 'Local'
              AND l.status = 'Available'
              AND l.excluded_from_asset_list = 0
              AND ($after_asset_id IS NULL OR a.id > $after_asset_id)
              AND NOT EXISTS (
                  SELECT 1
                  FROM asset_metadata m
                  WHERE m.asset_id = a.id
                    AND m.pipeline_version = $pipeline_version
                    AND m.source_size = a.size
                    AND m.source_modified_at = a.modified_at
              )
            GROUP BY
                a.id,
                a.original_filename,
                a.extension,
                a.mime_type,
                a.size,
                a.created_at,
                a.modified_at
            ORDER BY a.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pipeline_version", pipelineVersion);
        command.Parameters.AddWithValue(
            "$after_asset_id",
            afterAssetId is null ? DBNull.Value : afterAssetId.Value.ToString("D"));
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new MetadataCandidate(
                Guid.Parse(reader.GetString(0)),
                new DiscoveredFile(
                    reader.GetString(7),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4),
                    ParseTimestamp(reader.GetString(5)),
                    ParseTimestamp(reader.GetString(6)))));
        }

        return candidates;
    }

    public async Task<bool> SaveMetadataAsync(
        AssetMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.PipelineVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(metadata));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO asset_metadata(
                asset_id, extractor_name, pipeline_version, status,
                source_size, source_modified_at, metadata_json,
                error_message, extracted_at)
            SELECT
                $asset_id, $extractor_name, $pipeline_version, $status,
                $source_size, $source_modified_at, $metadata_json,
                $error_message, $extracted_at
            WHERE EXISTS (
                SELECT 1
                FROM assets
                WHERE id = $asset_id
                  AND size = $source_size
                  AND modified_at = $source_modified_at
            )
            ON CONFLICT(asset_id) DO UPDATE SET
                extractor_name = excluded.extractor_name,
                pipeline_version = excluded.pipeline_version,
                status = excluded.status,
                source_size = excluded.source_size,
                source_modified_at = excluded.source_modified_at,
                metadata_json = excluded.metadata_json,
                error_message = excluded.error_message,
                extracted_at = excluded.extracted_at;
            """;
        AddMetadataParameters(command, metadata);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<AssetMetadata?> GetMetadataAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                asset_id, extractor_name, pipeline_version, status,
                source_size, source_modified_at, metadata_json,
                error_message, extracted_at
            FROM asset_metadata
            WHERE asset_id = $asset_id;
            """;
        command.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMetadata(reader, Guid.Parse(reader.GetString(0)), 1)
            : null;
    }

    public async Task<AssetStatistics> GetLocalAssetStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        long assetCount;
        long availableLocalFileCount;
        long unavailableAssetCount;
        long totalSizeBytes;
        long videoAssetCount;
        long audioAssetCount;
        long imageAssetCount;
        long documentAssetCount;
        long otherAssetCount;
        long backedUpAssetCount;
        await using (var summaryCommand = connection.CreateCommand())
        {
            summaryCommand.CommandText =
                $"""
                WITH scoped_assets AS (
                    SELECT a.*
                    FROM assets a
                    WHERE a.hidden_from_asset_list = 0
                      AND EXISTS (
                          SELECT 1
                          FROM asset_locations l
                          WHERE l.asset_id = a.id
                            AND l.location_type = 'Local'
                            AND l.excluded_from_asset_list = 0
                      )
                )
                SELECT
                    COUNT(*),
                    COALESCE((
                        SELECT COUNT(*)
                        FROM asset_locations available_location
                        INNER JOIN scoped_assets available_asset
                            ON available_asset.id = available_location.asset_id
                        WHERE available_location.location_type = 'Local'
                          AND available_location.status = 'Available'
                          AND available_location.excluded_from_asset_list = 0
                    ), 0),
                    COALESCE(SUM(CASE
                        WHEN EXISTS (
                            SELECT 1
                            FROM asset_locations available_location
                            WHERE available_location.asset_id = a.id
                              AND available_location.location_type = 'Local'
                              AND available_location.status = 'Available'
                              AND available_location.excluded_from_asset_list = 0
                        ) THEN 0 ELSE 1 END), 0),
                    COALESCE((
                        SELECT SUM(available_asset.size)
                        FROM asset_locations available_location
                        INNER JOIN scoped_assets available_asset
                            ON available_asset.id = available_location.asset_id
                        WHERE available_location.location_type = 'Local'
                          AND available_location.status = 'Available'
                          AND available_location.excluded_from_asset_list = 0
                    ), 0),
                    COALESCE(SUM(CASE WHEN {VideoAssetPredicate} THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN {AudioAssetPredicate} THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN {ImageAssetPredicate} THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN {DocumentAssetPredicate} THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN NOT (
                        {VideoAssetPredicate}
                        OR {AudioAssetPredicate}
                        OR {ImageAssetPredicate}
                        OR {DocumentAssetPredicate}
                    ) THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN EXISTS (
                        SELECT 1
                        FROM object_storage_locations osl
                        WHERE osl.asset_id = a.id
                          AND osl.status = 'Healthy'
                    ) THEN 1 ELSE 0 END), 0)
                FROM scoped_assets a;
                """;

            await using var reader = await summaryCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Unable to calculate local asset statistics.");
            }

            assetCount = reader.GetInt64(0);
            availableLocalFileCount = reader.GetInt64(1);
            unavailableAssetCount = reader.GetInt64(2);
            totalSizeBytes = reader.GetInt64(3);
            videoAssetCount = reader.GetInt64(4);
            audioAssetCount = reader.GetInt64(5);
            imageAssetCount = reader.GetInt64(6);
            documentAssetCount = reader.GetInt64(7);
            otherAssetCount = reader.GetInt64(8);
            backedUpAssetCount = reader.GetInt64(9);
        }

        long videoDurationMilliseconds = 0;
        await using var metadataCommand = connection.CreateCommand();
        metadataCommand.CommandText =
            """
            SELECT
                a.id,
                m.extractor_name,
                m.pipeline_version,
                m.status,
                m.source_size,
                m.source_modified_at,
                m.metadata_json,
                m.error_message,
                m.extracted_at
            FROM assets a
            INNER JOIN asset_metadata m
                ON m.asset_id = a.id
               AND m.pipeline_version = $pipeline_version
               AND m.source_size = a.size
               AND m.source_modified_at = a.modified_at
            WHERE a.hidden_from_asset_list = 0
              AND EXISTS (
                  SELECT 1
                  FROM asset_locations l
                  WHERE l.asset_id = a.id
                    AND l.location_type = 'Local'
                    AND l.excluded_from_asset_list = 0
              )
              AND m.status = $metadata_status;
            """;
        metadataCommand.Parameters.AddWithValue(
            "$pipeline_version",
            MetadataPipeline.CurrentVersion);
        metadataCommand.Parameters.AddWithValue(
            "$metadata_status",
            MetadataExtractionStatus.Extracted.ToString());

        await using var metadataReader =
            await metadataCommand.ExecuteReaderAsync(cancellationToken);
        while (await metadataReader.ReadAsync(cancellationToken))
        {
            var metadata = ReadMetadata(
                metadataReader,
                Guid.Parse(metadataReader.GetString(0)),
                1);
            if (metadata?.Content?.Kind != AssetMediaKind.Video)
            {
                continue;
            }

            var duration = metadata.Content.DurationMilliseconds;
            if (duration is not > 0)
            {
                continue;
            }

            videoDurationMilliseconds =
                long.MaxValue - videoDurationMilliseconds < duration.Value
                    ? long.MaxValue
                    : videoDurationMilliseconds + duration.Value;
        }

        return new AssetStatistics(
            assetCount,
            availableLocalFileCount,
            unavailableAssetCount,
            totalSizeBytes,
            videoAssetCount,
            audioAssetCount,
            imageAssetCount,
            documentAssetCount,
            otherAssetCount,
            backedUpAssetCount,
            videoDurationMilliseconds);
    }

    public Task<long> GetAssetListCountAsync(
        CancellationToken cancellationToken = default)
    {
        return GetAssetListCountAsync(AssetListFilter.Empty, cancellationToken);
    }

    public async Task<long> GetAssetListCountAsync(
        AssetListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var filterSql = CreateAssetFilterSql(filter);
        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM asset_locations l
            INNER JOIN assets a ON a.id = l.asset_id
            WHERE l.location_type = 'Local'
              AND l.excluded_from_asset_list = 0
              AND a.hidden_from_asset_list = 0
              {filterSql};
            """;
        AddAssetFilterParameters(command, filter);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<int> HideAssetsFromListAsync(
        IReadOnlyCollection<Guid> assetIds,
        DateTimeOffset hiddenAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var uniqueAssetIds = assetIds.Distinct().ToArray();
        if (uniqueAssetIds.Length == 0)
        {
            return 0;
        }

        if (uniqueAssetIds.Length > 1_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(assetIds),
                "一次最多可从资产列表移除 1000 个资产。");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var idParameters = uniqueAssetIds
            .Select((_, index) => $"$asset_id_{index}")
            .ToArray();
        command.CommandText =
            $"""
            UPDATE assets
            SET hidden_from_asset_list = 1,
                hidden_from_asset_list_at = $hidden_at
            WHERE hidden_from_asset_list = 0
              AND id IN ({string.Join(", ", idParameters)});
            """;
        command.Parameters.AddWithValue("$hidden_at", hiddenAt.ToString("O"));
        for (var index = 0; index < uniqueAssetIds.Length; index++)
        {
            command.Parameters.AddWithValue(
                idParameters[index],
                uniqueAssetIds[index].ToString("D"));
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssetDirectorySummary>> ListAssetDirectoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var directories = new Dictionary<string, AssetDirectoryAccumulator>(comparer);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT l.path, l.status, a.size, a.modified_at
            FROM asset_locations l
            INNER JOIN assets a ON a.id = l.asset_id
            WHERE l.location_type = 'Local'
              AND l.excluded_from_asset_list = 0
              AND a.hidden_from_asset_list = 0
            ORDER BY l.path;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var directoryPath = Path.GetDirectoryName(reader.GetString(0));
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            if (!directories.TryGetValue(directoryPath, out var accumulator))
            {
                accumulator = new AssetDirectoryAccumulator(directoryPath);
                directories.Add(directoryPath, accumulator);
            }

            accumulator.Add(
                Enum.Parse<AssetLocationStatus>(reader.GetString(1)),
                reader.GetInt64(2),
                ParseTimestamp(reader.GetString(3)));
        }

        return directories.Values
            .Select(directory => directory.ToSummary())
            .OrderBy(directory => directory.Path, comparer)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> ListAssetExtensionsAsync(
        AssetFileTypeFilter fileType = AssetFileTypeFilter.All,
        CancellationToken cancellationToken = default)
    {
        var extensions = new List<string>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var fileTypeCondition = CreateAssetFileTypeCondition(fileType);
        command.CommandText =
            $"""
            SELECT lower(a.extension)
            FROM assets a
            WHERE length(trim(a.extension)) > 0
              AND a.hidden_from_asset_list = 0
              {(fileTypeCondition is null ? string.Empty : $"AND {fileTypeCondition}")}
              AND EXISTS (
                  SELECT 1
                  FROM asset_locations l
                  WHERE l.asset_id = a.id
                    AND l.location_type = 'Local'
                    AND l.excluded_from_asset_list = 0
              )
            GROUP BY lower(a.extension)
            ORDER BY lower(a.extension);
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            extensions.Add(reader.GetString(0));
        }

        return extensions;
    }

    public Task<IReadOnlyList<AssetListItem>> ListAssetsAsync(
        int limit,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        return ListAssetsAsync(
            AssetListFilter.Empty,
            limit,
            offset,
            cancellationToken);
    }

    public async Task<IReadOnlyList<AssetListItem>> ListAssetsAsync(
        AssetListFilter filter,
        int limit,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (limit is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var assets = new List<AssetListItem>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var filterSql = CreateAssetFilterSql(filter);
        command.CommandText =
            $"""
            SELECT
                a.id,
                a.original_filename,
                a.extension,
                a.mime_type,
                a.size,
                a.sha256,
                a.modified_at,
                a.discovered_at,
                l.path,
                l.ownership,
                l.status,
                a.status,
                EXISTS (
                    SELECT 1
                    FROM object_storage_locations osl
                    WHERE osl.asset_id = a.id
                      AND osl.status = 'Healthy'
                ) AS has_healthy_backup,
                m.extractor_name,
                m.pipeline_version,
                m.status,
                m.source_size,
                m.source_modified_at,
                m.metadata_json,
                m.error_message,
                m.extracted_at,
                COALESCE((
                    SELECT json_group_array(tag_name)
                    FROM (
                        SELECT t.name AS tag_name
                        FROM asset_tag_links atl
                        INNER JOIN asset_tags t ON t.id = atl.tag_id
                        WHERE atl.asset_id = a.id
                        ORDER BY t.name COLLATE NOCASE, t.id
                    )
                ), '[]') AS tags_json,
                COALESCE((
                    SELECT json_group_array(project_name)
                    FROM (
                        SELECT c.name AS project_name
                        FROM asset_collection_items ci
                        INNER JOIN asset_collections c ON c.id = ci.collection_id
                        WHERE ci.asset_id = a.id
                        ORDER BY c.name COLLATE NOCASE, c.id
                    )
                ), '[]') AS project_names_json,
                COALESCE((
                    SELECT json_group_array(provider)
                    FROM (
                        SELECT DISTINCT sp.provider AS provider
                        FROM object_storage_locations osl
                        INNER JOIN storage_profiles sp
                            ON sp.id = osl.storage_profile_id
                        WHERE osl.asset_id = a.id
                          AND osl.status = 'Healthy'
                        ORDER BY sp.provider COLLATE NOCASE
                    )
                ), '[]') AS backup_providers_json,
                (
                    SELECT COALESCE(
                        osl.last_verified_at,
                        osl.updated_at,
                        osl.created_at)
                    FROM object_storage_locations osl
                    WHERE osl.asset_id = a.id
                      AND osl.status = 'Healthy'
                    ORDER BY
                        julianday(COALESCE(
                            osl.last_verified_at,
                            osl.updated_at,
                            osl.created_at)) DESC,
                        osl.id
                    LIMIT 1
                ) AS latest_healthy_backup_at
            FROM assets a
            INNER JOIN asset_locations l ON l.asset_id = a.id
            LEFT JOIN asset_metadata m
                ON m.asset_id = a.id
               AND m.pipeline_version = $metadata_pipeline_version
               AND m.source_size = a.size
               AND m.source_modified_at = a.modified_at
            WHERE l.location_type = 'Local'
              AND l.excluded_from_asset_list = 0
              AND a.hidden_from_asset_list = 0
              {filterSql}
            ORDER BY
                a.discovered_at DESC,
                a.original_filename,
                l.path,
                a.id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue(
            "$metadata_pipeline_version",
            MetadataPipeline.CurrentVersion);
        AddAssetFilterParameters(command, filter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            assets.Add(new AssetListItem(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                ParseTimestamp(reader.GetString(6)),
                ParseTimestamp(reader.GetString(7)),
                reader.GetString(8),
                Enum.Parse<AssetLocationOwnership>(reader.GetString(9)),
                Enum.Parse<AssetLocationStatus>(reader.GetString(10)),
                Enum.Parse<AssetStatus>(reader.GetString(11)),
                reader.GetInt64(12) != 0,
                ReadMetadata(reader, Guid.Parse(reader.GetString(0)), 13))
            {
                Tags = ReadJsonStringArray(reader, 21),
                ProjectNames = ReadJsonStringArray(reader, 22),
                HealthyBackupProviders = ReadJsonStringArray(reader, 23),
                LatestHealthyBackupAt = reader.IsDBNull(24)
                    ? null
                    : ParseTimestamp(reader.GetString(24))
            });
        }

        return assets;
    }

    public async Task<IReadOnlyList<ExactDuplicateGroup>> ListExactDuplicateGroupsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var groups = new List<ExactDuplicateGroup>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var groupCommand = connection.CreateCommand();
        groupCommand.CommandText =
            """
            SELECT a.sha256, a.size
            FROM assets a
            WHERE a.sha256 IS NOT NULL
              AND a.hidden_from_asset_list = 0
              AND EXISTS (
                  SELECT 1
                  FROM asset_locations visible_location
                  WHERE visible_location.asset_id = a.id
                    AND visible_location.location_type = 'Local'
                    AND visible_location.excluded_from_asset_list = 0
              )
            GROUP BY a.sha256, a.size
            HAVING COUNT(*) > 1
            ORDER BY COUNT(*) DESC, a.size DESC
            LIMIT $limit;
            """;
        groupCommand.Parameters.AddWithValue("$limit", limit);

        var candidates = new List<(string Sha256, long Size)>();
        await using (var reader = await groupCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        foreach (var candidate in candidates)
        {
            await using var itemCommand = connection.CreateCommand();
            itemCommand.CommandText =
                """
                SELECT
                    a.id,
                    a.original_filename,
                    l.path,
                    a.modified_at,
                    l.status
                FROM assets a
                INNER JOIN asset_locations l ON l.asset_id = a.id
                WHERE a.sha256 = $sha256
                  AND a.size = $size
                  AND l.location_type = 'Local'
                  AND l.excluded_from_asset_list = 0
                  AND a.hidden_from_asset_list = 0
                ORDER BY l.path;
                """;
            itemCommand.Parameters.AddWithValue("$sha256", candidate.Sha256);
            itemCommand.Parameters.AddWithValue("$size", candidate.Size);

            var items = new List<DuplicateAssetItem>();
            await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken);
            while (await itemReader.ReadAsync(cancellationToken))
            {
                items.Add(new DuplicateAssetItem(
                    Guid.Parse(itemReader.GetString(0)),
                    itemReader.GetString(1),
                    itemReader.GetString(2),
                    ParseTimestamp(itemReader.GetString(3)),
                    Enum.Parse<AssetLocationStatus>(itemReader.GetString(4))));
            }

            if (items.Count > 1)
            {
                groups.Add(new ExactDuplicateGroup(candidate.Sha256, candidate.Size, items));
            }
        }

        return groups;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<ExistingAsset?> FindAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        string pathKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT a.id, a.size, a.modified_at, a.sha256
            FROM asset_locations l
            INNER JOIN assets a ON a.id = l.asset_id
            WHERE l.device_id = $device_id AND l.path_key = $path_key
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$path_key", pathKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExistingAsset(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt64(1),
            ParseTimestamp(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task InsertAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        DiscoveredFile file,
        DateTimeOffset discoveredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO assets(
                id, original_filename, mime_type, extension, size, sha256,
                created_at, modified_at, discovered_at, status)
            VALUES (
                $id, $original_filename, $mime_type, $extension, $size, NULL,
                $created_at, $modified_at, $discovered_at, $status);
            """;
        AddAssetParameters(command, assetId, file, discoveredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        DiscoveredFile file,
        DateTimeOffset discoveredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE assets
            SET original_filename = $original_filename,
                mime_type = $mime_type,
                extension = $extension,
                sha256 = CASE
                    WHEN size = $size AND modified_at = $modified_at THEN sha256
                    ELSE NULL
                END,
                size = $size,
                created_at = $created_at,
                modified_at = $modified_at,
                discovered_at = $discovered_at,
                status = $status
            WHERE id = $id;
            """;
        AddAssetParameters(command, assetId, file, discoveredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLocationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid locationId,
        Guid assetId,
        string deviceId,
        string path,
        string pathKey,
        LocalPathVolumeBinding? volumeBinding,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO asset_locations(
                id, asset_id, location_type, ownership, device_id, path, path_key,
                status, last_seen_at, last_verified_at,
                volume_id, volume_relative_path)
            VALUES (
                $id, $asset_id, 'Local', 'External', $device_id, $path, $path_key,
                'Available', $last_seen_at, NULL,
                $volume_id, $volume_relative_path);
            """;
        command.Parameters.AddWithValue("$id", locationId.ToString("D"));
        command.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$path_key", pathKey);
        AddVolumeBindingParameters(command, volumeBinding);
        command.Parameters.AddWithValue("$last_seen_at", seenAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateLocationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        string path,
        string pathKey,
        LocalPathVolumeBinding? volumeBinding,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE asset_locations
            SET path = $path,
                status = 'Available',
                last_seen_at = $last_seen_at,
                excluded_from_asset_list = 0,
                excluded_from_asset_list_at = NULL,
                volume_id = CASE
                    WHEN $volume_id IS NULL THEN volume_id
                    ELSE $volume_id
                END,
                volume_relative_path = CASE
                    WHEN $volume_id IS NULL THEN volume_relative_path
                    ELSE $volume_relative_path
                END
            WHERE device_id = $device_id AND path_key = $path_key;
            """;
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$path_key", pathKey);
        AddVolumeBindingParameters(command, volumeBinding);
        command.Parameters.AddWithValue("$last_seen_at", seenAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddAssetParameters(
        SqliteCommand command,
        Guid assetId,
        DiscoveredFile file,
        DateTimeOffset discoveredAt)
    {
        command.Parameters.AddWithValue("$id", assetId.ToString("D"));
        command.Parameters.AddWithValue("$original_filename", file.OriginalFilename);
        command.Parameters.AddWithValue("$mime_type", (object?)file.MimeType ?? DBNull.Value);
        command.Parameters.AddWithValue("$extension", file.Extension);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$created_at", file.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$modified_at", file.ModifiedAt.ToString("O"));
        command.Parameters.AddWithValue("$discovered_at", discoveredAt.ToString("O"));
        command.Parameters.AddWithValue("$status", AssetStatus.Indexed.ToString());
    }

    private static void AddMetadataParameters(
        SqliteCommand command,
        AssetMetadata metadata)
    {
        command.Parameters.AddWithValue("$asset_id", metadata.AssetId.ToString("D"));
        command.Parameters.AddWithValue("$extractor_name", metadata.ExtractorName);
        command.Parameters.AddWithValue("$pipeline_version", metadata.PipelineVersion);
        command.Parameters.AddWithValue("$status", metadata.Status.ToString());
        command.Parameters.AddWithValue("$source_size", metadata.SourceSize);
        command.Parameters.AddWithValue(
            "$source_modified_at",
            metadata.SourceModifiedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$metadata_json",
            metadata.Content is null
                ? DBNull.Value
                : JsonSerializer.Serialize(metadata.Content));
        command.Parameters.AddWithValue(
            "$error_message",
            (object?)metadata.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$extracted_at", metadata.ExtractedAt.ToString("O"));
    }

    private static AssetMetadata? ReadMetadata(
        SqliteDataReader reader,
        Guid assetId,
        int offset)
    {
        if (reader.IsDBNull(offset))
        {
            return null;
        }

        var content = reader.IsDBNull(offset + 5)
            ? null
            : JsonSerializer.Deserialize<AssetMetadataContent>(reader.GetString(offset + 5));

        return new AssetMetadata(
            assetId,
            reader.GetString(offset),
            reader.GetInt32(offset + 1),
            Enum.Parse<MetadataExtractionStatus>(reader.GetString(offset + 2)),
            reader.GetInt64(offset + 3),
            ParseTimestamp(reader.GetString(offset + 4)),
            content,
            ParseTimestamp(reader.GetString(offset + 7)),
            reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6));
    }

    private static void AddScanJobParameters(SqliteCommand command, ScanJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id.ToString("D"));
        command.Parameters.AddWithValue("$scan_root_id", job.ScanRootId.ToString("D"));
        command.Parameters.AddWithValue("$status", job.Status.ToString());
        command.Parameters.AddWithValue("$started_at", job.StartedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$finished_at",
            job.FinishedAt is null ? DBNull.Value : job.FinishedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$files_discovered", job.FilesDiscovered);
        command.Parameters.AddWithValue("$files_processed", job.FilesProcessed);
        command.Parameters.AddWithValue("$errors", job.Errors);
        command.Parameters.AddWithValue(
            "$error_message",
            (object?)job.ErrorMessage ?? DBNull.Value);
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string CreatePathKey(string path)
    {
        return path.ToUpperInvariant();
    }

    private static string GetFingerprintModeFilter(FingerprintMode mode)
    {
        return mode switch
        {
            FingerprintMode.Complete => string.Empty,
            FingerprintMode.DuplicateCandidates =>
                """
                AND a.size IN (
                    SELECT a2.size
                    FROM assets a2
                    WHERE EXISTS (
                        SELECT 1
                        FROM asset_locations l2
                        WHERE l2.asset_id = a2.id
                          AND l2.location_type = 'Local'
                          AND l2.status = 'Available'
                          AND l2.excluded_from_asset_list = 0
                    )
                    GROUP BY a2.size
                    HAVING COUNT(*) > 1
                )
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    private static string CreateAssetFilterSql(AssetListFilter filter)
    {
        var conditions = new List<string>();
        var fileTypeCondition = CreateAssetFileTypeCondition(filter.FileType);
        if (fileTypeCondition is not null)
        {
            conditions.Add(fileTypeCondition);
        }

        if (filter.CreatedFrom is not null)
        {
            conditions.Add("julianday(a.created_at) >= julianday($created_from)");
        }

        if (filter.CreatedBefore is not null)
        {
            conditions.Add("julianday(a.created_at) < julianday($created_before)");
        }

        if (filter.Extension is not null)
        {
            conditions.Add("lower(a.extension) = $extension");
        }

        if (filter.FilenameContains is not null)
        {
            conditions.Add(
                "instr(lower(a.original_filename), $filename_contains) > 0");
        }

        if (filter.TagId is not null)
        {
            conditions.Add(
                """
                EXISTS (
                    SELECT 1
                    FROM asset_tag_links atl_filter
                    WHERE atl_filter.asset_id = a.id
                      AND atl_filter.tag_id = $tag_id
                )
                """);
        }

        return conditions.Count == 0
            ? string.Empty
            : $"AND {string.Join(" AND ", conditions)}";
    }

    private static string? CreateAssetFileTypeCondition(
        AssetFileTypeFilter fileType)
    {
        return fileType switch
        {
            AssetFileTypeFilter.All => null,
            AssetFileTypeFilter.Video => VideoAssetPredicate,
            AssetFileTypeFilter.Audio => AudioAssetPredicate,
            AssetFileTypeFilter.Image => ImageAssetPredicate,
            AssetFileTypeFilter.Document => DocumentAssetPredicate,
            AssetFileTypeFilter.Other =>
                $"NOT ({VideoAssetPredicate} OR {AudioAssetPredicate} OR {ImageAssetPredicate} OR {DocumentAssetPredicate})",
            _ => throw new ArgumentOutOfRangeException(nameof(fileType))
        };
    }

    private static string CreateExtensionWhitelistCondition(int extensionCount)
    {
        if (extensionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extensionCount));
        }

        var parameters = Enumerable.Range(0, extensionCount)
            .Select(index => $"$scan_extension_{index}");
        return $"lower(COALESCE(a.extension, '')) IN ({string.Join(", ", parameters)})";
    }

    private static string? CreateScanFileFilterCondition(ScanFileFilter fileFilter)
    {
        if (fileFilter.FileTypeFilters.Count == ScanFileFilter.AllFileTypes.Count)
        {
            return null;
        }

        var conditions = fileFilter.FileTypeFilters
            .Select(CreateAssetFileTypeCondition)
            .Where(condition => condition is not null)
            .Cast<string>()
            .ToList();
        if (fileFilter.UsesExtensionWhitelist)
        {
            conditions.Add(CreateExtensionWhitelistCondition(
                fileFilter.ExtensionWhitelist.Count));
        }

        return conditions.Count == 1
            ? conditions[0]
            : $"({string.Join(" OR ", conditions)})";
    }

    private static void AddAssetFilterParameters(
        SqliteCommand command,
        AssetListFilter filter)
    {
        if (filter.CreatedFrom is not null)
        {
            command.Parameters.AddWithValue(
                "$created_from",
                filter.CreatedFrom.Value.UtcDateTime.ToString("O"));
        }

        if (filter.CreatedBefore is not null)
        {
            command.Parameters.AddWithValue(
                "$created_before",
                filter.CreatedBefore.Value.UtcDateTime.ToString("O"));
        }

        if (filter.Extension is not null)
        {
            command.Parameters.AddWithValue("$extension", filter.Extension);
        }

        if (filter.FilenameContains is not null)
        {
            command.Parameters.AddWithValue(
                "$filename_contains",
                filter.FilenameContains.ToLowerInvariant());
        }

        if (filter.TagId is not null)
        {
            command.Parameters.AddWithValue(
                "$tag_id",
                filter.TagId.Value.ToString("D"));
        }
    }

    private static IReadOnlyList<string> ReadJsonStringArray(
        SqliteDataReader reader,
        int ordinal)
    {
        return JsonSerializer.Deserialize<string[]>(reader.GetString(ordinal)) ?? [];
    }

    private const string VideoAssetPredicate =
        "COALESCE(a.mime_type, '') LIKE 'video/%'";

    private const string AudioAssetPredicate =
        "COALESCE(a.mime_type, '') LIKE 'audio/%'";

    private const string ImageAssetPredicate =
        "COALESCE(a.mime_type, '') LIKE 'image/%'";

    private const string DocumentAssetPredicate =
        """
        (
            COALESCE(a.mime_type, '') LIKE 'text/%'
            OR lower(COALESCE(a.extension, '')) IN (
                '.csv', '.doc', '.docx', '.htm', '.html', '.json', '.md',
                '.odt', '.ods', '.odp', '.pdf', '.ppt', '.pptx', '.rtf',
                '.srt', '.tsv', '.txt', '.xls', '.xlsx', '.xml'
            )
        )
        """;

    private sealed record ExistingAsset(
        Guid Id,
        long Size,
        DateTimeOffset ModifiedAt,
        string? Sha256);

    private sealed class AssetDirectoryAccumulator(string path)
    {
        private long _assetCount;
        private long _availableAssetCount;
        private long _missingAssetCount;
        private long _availableSizeBytes;
        private DateTimeOffset _latestModifiedAt = DateTimeOffset.MinValue;

        public void Add(
            AssetLocationStatus status,
            long size,
            DateTimeOffset modifiedAt)
        {
            _assetCount++;
            if (status == AssetLocationStatus.Available)
            {
                _availableAssetCount++;
                _availableSizeBytes = long.MaxValue - _availableSizeBytes < size
                    ? long.MaxValue
                    : _availableSizeBytes + size;
            }
            else if (status == AssetLocationStatus.Missing)
            {
                _missingAssetCount++;
            }

            if (modifiedAt > _latestModifiedAt)
            {
                _latestModifiedAt = modifiedAt;
            }
        }

        public AssetDirectorySummary ToSummary()
        {
            return new AssetDirectorySummary(
                path,
                _assetCount,
                _availableAssetCount,
                _missingAssetCount,
                _availableSizeBytes,
                _latestModifiedAt);
        }
    }
}
