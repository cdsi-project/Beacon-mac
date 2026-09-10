using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Metadata;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository : IAssetCollectionRepository
{
    public async Task<bool> CreateAssetCollectionAsync(
        AssetCollection collection,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO asset_collections (
                id, name, type, created_at, updated_at)
            VALUES (
                $id, $name, $type, $created_at, $updated_at);
            """;
        command.Parameters.AddWithValue("$id", collection.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", collection.Name);
        command.Parameters.AddWithValue("$type", collection.Type.ToString());
        command.Parameters.AddWithValue("$created_at", collection.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", collection.UpdatedAt.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        foreach (var profileId in collection.BackupProfileIds.Distinct())
        {
            await using var bindingCommand = connection.CreateCommand();
            bindingCommand.Transaction = transaction;
            bindingCommand.CommandText =
                """
                INSERT INTO asset_collection_backup_profiles (
                    collection_id, profile_id, added_at)
                VALUES ($collection_id, $profile_id, $added_at);
                """;
            bindingCommand.Parameters.AddWithValue(
                "$collection_id",
                collection.Id.ToString("D"));
            bindingCommand.Parameters.AddWithValue("$profile_id", profileId.ToString("D"));
            bindingCommand.Parameters.AddWithValue(
                "$added_at",
                collection.CreatedAt.ToString("O"));
            await bindingCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<AssetCollection?> GetAssetCollectionAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, type, created_at, updated_at
            FROM asset_collections
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", collectionId.ToString("D"));

        AssetCollection? collection;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            collection = await reader.ReadAsync(cancellationToken)
                ? ReadAssetCollection(reader)
                : null;
        }

        if (collection is null)
        {
            return null;
        }

        return collection with
        {
            BackupProfileIds = await ListAssetCollectionBackupProfileIdsAsync(
                connection,
                collectionId,
                cancellationToken)
        };
    }

    public async Task<bool> UpdateAssetCollectionAsync(
        AssetCollection collection,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE OR IGNORE asset_collections
            SET name = $name,
                type = $type,
                updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", collection.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", collection.Name);
        command.Parameters.AddWithValue("$type", collection.Type.ToString());
        command.Parameters.AddWithValue("$updated_at", collection.UpdatedAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<AssetCollectionSummary>> ListAssetCollectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var collections = new List<AssetCollectionSummary>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                c.id,
                c.name,
                c.type,
                COUNT(ci.asset_id),
                COALESCE(SUM(a.size), 0),
                COALESCE(SUM(CASE WHEN EXISTS (
                    SELECT 1
                    FROM object_storage_locations osl
                    WHERE osl.asset_id = a.id
                      AND osl.status = 'Healthy'
                ) THEN 1 ELSE 0 END), 0),
                c.created_at,
                c.updated_at
            FROM asset_collections c
            LEFT JOIN asset_collection_items ci ON ci.collection_id = c.id
            LEFT JOIN assets a ON a.id = ci.asset_id
            GROUP BY c.id, c.name, c.type, c.created_at, c.updated_at
            ORDER BY c.updated_at DESC, c.name;
            """;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                collections.Add(new AssetCollectionSummary(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    Enum.Parse<AssetCollectionType>(reader.GetString(2)),
                    reader.GetInt32(3),
                    reader.GetInt64(4),
                    reader.GetInt32(5),
                    ParseTimestamp(reader.GetString(6)),
                    ParseTimestamp(reader.GetString(7))));
            }
        }

        var backupTargets = await ListAssetCollectionBackupTargetsAsync(
            connection,
            cancellationToken);
        return collections
            .Select(collection => collection with
            {
                BackupTargets = backupTargets.GetValueOrDefault(collection.Id) ?? []
            })
            .ToArray();
    }

    public async Task<bool> DeleteAssetCollectionAsync(
        Guid collectionId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken = default)
    {
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            await using (var transaction =
                (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken))
            {
                int audited;
                await using (var auditCommand = connection.CreateCommand())
                {
                    auditCommand.Transaction = transaction;
                    auditCommand.CommandText =
                        """
                        INSERT INTO asset_collection_deletion_audit (
                            collection_id, name, type, asset_count, deleted_at)
                        SELECT
                            c.id,
                            c.name,
                            c.type,
                            COUNT(ci.asset_id),
                            $deleted_at
                        FROM asset_collections c
                        LEFT JOIN asset_collection_items ci ON ci.collection_id = c.id
                        WHERE c.id = $id
                        GROUP BY c.id, c.name, c.type;
                        """;
                    auditCommand.Parameters.AddWithValue(
                        "$id",
                        collectionId.ToString("D"));
                    auditCommand.Parameters.AddWithValue(
                        "$deleted_at",
                        deletedAt.ToString("O"));
                    audited = await auditCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                if (audited == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }

                bool deleted;
                await using (var deleteCommand = connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText =
                        """
                        DELETE FROM asset_collections
                        WHERE id = $id;
                        """;
                    deleteCommand.Parameters.AddWithValue(
                        "$id",
                        collectionId.ToString("D"));
                    deleted = await deleteCommand.ExecuteNonQueryAsync(cancellationToken) == 1;
                }

                if (!deleted)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }

                await transaction.CommitAsync(cancellationToken);
            }
        }

        return true;
    }

    public async Task<int> AddAssetsToCollectionAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> assetIds,
        DateTimeOffset addedAt,
        CancellationToken cancellationToken = default)
    {
        if (assetIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        var added = 0;
        foreach (var assetId in assetIds.Distinct())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO asset_collection_items (
                    collection_id, asset_id, added_at)
                VALUES ($collection_id, $asset_id, $added_at);
                """;
            command.Parameters.AddWithValue("$collection_id", collectionId.ToString("D"));
            command.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));
            command.Parameters.AddWithValue("$added_at", addedAt.ToString("O"));
            added += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (added > 0)
        {
            await UpdateAssetCollectionTimestampAsync(
                connection,
                transaction,
                collectionId,
                addedAt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    public async Task<int> RemoveAssetsFromCollectionAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> assetIds,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (assetIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        var removed = 0;
        foreach (var assetId in assetIds.Distinct())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                DELETE FROM asset_collection_items
                WHERE collection_id = $collection_id
                  AND asset_id = $asset_id;
                """;
            command.Parameters.AddWithValue("$collection_id", collectionId.ToString("D"));
            command.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));
            removed += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (removed > 0)
        {
            await UpdateAssetCollectionTimestampAsync(
                connection,
                transaction,
                collectionId,
                updatedAt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<IReadOnlyList<AssetCollectionMember>> ListAssetCollectionMembersAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var members = new List<AssetCollectionMember>();
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
                ci.added_at
            FROM asset_collection_items ci
            INNER JOIN assets a ON a.id = ci.asset_id
            INNER JOIN asset_locations l ON l.id = (
                SELECT l2.id
                FROM asset_locations l2
                WHERE l2.asset_id = a.id
                  AND l2.location_type = 'Local'
                ORDER BY
                    CASE l2.status
                        WHEN 'Available' THEN 0
                        WHEN 'Unverified' THEN 1
                        ELSE 2
                    END,
                    l2.last_seen_at DESC
                LIMIT 1
            )
            LEFT JOIN asset_metadata m
                ON m.asset_id = a.id
               AND m.pipeline_version = $metadata_pipeline_version
               AND m.source_size = a.size
               AND m.source_modified_at = a.modified_at
            WHERE ci.collection_id = $collection_id
            ORDER BY ci.added_at, a.original_filename;
            """;
        command.Parameters.AddWithValue("$collection_id", collectionId.ToString("D"));
        command.Parameters.AddWithValue(
            "$metadata_pipeline_version",
            MetadataPipeline.CurrentVersion);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var assetId = Guid.Parse(reader.GetString(0));
            var asset = new AssetListItem(
                assetId,
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
                ReadMetadata(reader, assetId, 13))
            {
                Tags = ReadJsonStringArray(reader, 21)
            };
            members.Add(new AssetCollectionMember(
                collectionId,
                asset,
                ParseTimestamp(reader.GetString(22))));
        }

        return members;
    }

    private static AssetCollection ReadAssetCollection(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new AssetCollection(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Enum.Parse<AssetCollectionType>(reader.GetString(2)),
            ParseTimestamp(reader.GetString(3)),
            ParseTimestamp(reader.GetString(4)));
    }

    private static async Task<IReadOnlyList<Guid>> ListAssetCollectionBackupProfileIdsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        var profileIds = new List<Guid>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT profile_id
            FROM asset_collection_backup_profiles
            WHERE collection_id = $collection_id
            ORDER BY added_at, profile_id;
            """;
        command.Parameters.AddWithValue("$collection_id", collectionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profileIds.Add(Guid.Parse(reader.GetString(0)));
        }

        return profileIds;
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<AssetCollectionBackupTarget>>>
        ListAssetCollectionBackupTargetsAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        var targets = new Dictionary<Guid, List<AssetCollectionBackupTarget>>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                binding.collection_id,
                profile.id,
                profile.display_name,
                profile.provider
            FROM asset_collection_backup_profiles binding
            INNER JOIN storage_profiles profile ON profile.id = binding.profile_id
            ORDER BY binding.added_at, profile.display_name, profile.id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var collectionId = Guid.Parse(reader.GetString(0));
            if (!targets.TryGetValue(collectionId, out var collectionTargets))
            {
                collectionTargets = [];
                targets.Add(collectionId, collectionTargets);
            }

            collectionTargets.Add(new AssetCollectionBackupTarget(
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                Enum.Parse<CDSI.Agent.Core.Storage.ObjectStorageProvider>(
                    reader.GetString(3))));
        }

        return targets.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<AssetCollectionBackupTarget>)pair.Value);
    }

    private static async Task UpdateAssetCollectionTimestampAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        Guid collectionId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE asset_collections
            SET updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", collectionId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
