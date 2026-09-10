using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository : IAssetTagRepository
{
    public async Task<IReadOnlyList<AssetTagSummary>> ListAssetTagsAsync(
        CancellationToken cancellationToken = default)
    {
        var tags = new List<AssetTagSummary>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                t.id,
                t.name,
                COUNT(CASE
                    WHEN a.hidden_from_asset_list = 0
                     AND EXISTS (
                         SELECT 1
                         FROM asset_locations l
                         WHERE l.asset_id = a.id
                           AND l.location_type = 'Local'
                     )
                    THEN 1
                END)
            FROM asset_tags t
            LEFT JOIN asset_tag_links atl ON atl.tag_id = t.id
            LEFT JOIN assets a ON a.id = atl.asset_id
            GROUP BY t.id, t.name
            ORDER BY t.name COLLATE NOCASE, t.id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(new AssetTagSummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return tags;
    }

    public async Task<int> AssignAssetTagAsync(
        AssetTag tag,
        IReadOnlyCollection<Guid> assetIds,
        DateTimeOffset taggedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var ids = ValidateAssetTagIds(assetIds);
        if (ids.Length == 0)
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var tagCommand = connection.CreateCommand())
        {
            tagCommand.Transaction = (SqliteTransaction)transaction;
            tagCommand.CommandText =
                """
                INSERT INTO asset_tags(
                    id, name, normalized_name, created_at, updated_at)
                VALUES (
                    $id, $name, $normalized_name, $created_at, $updated_at)
                ON CONFLICT(normalized_name) DO NOTHING;
                """;
            tagCommand.Parameters.AddWithValue("$id", tag.Id.ToString("D"));
            tagCommand.Parameters.AddWithValue("$name", tag.Name);
            tagCommand.Parameters.AddWithValue("$normalized_name", tag.NormalizedName);
            tagCommand.Parameters.AddWithValue("$created_at", tag.CreatedAt.ToString("O"));
            tagCommand.Parameters.AddWithValue("$updated_at", tag.UpdatedAt.ToString("O"));
            await tagCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        string tagId;
        await using (var findTagCommand = connection.CreateCommand())
        {
            findTagCommand.Transaction = (SqliteTransaction)transaction;
            findTagCommand.CommandText =
                "SELECT id FROM asset_tags WHERE normalized_name = $normalized_name;";
            findTagCommand.Parameters.AddWithValue("$normalized_name", tag.NormalizedName);
            tagId = (string?)await findTagCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("无法读取刚保存的资产标签。");
        }

        var assigned = 0;
        foreach (var assetId in ids)
        {
            await using var linkCommand = connection.CreateCommand();
            linkCommand.Transaction = (SqliteTransaction)transaction;
            linkCommand.CommandText =
                """
                INSERT INTO asset_tag_links(asset_id, tag_id, tagged_at)
                SELECT $asset_id, $tag_id, $tagged_at
                WHERE EXISTS (SELECT 1 FROM assets WHERE id = $asset_id)
                ON CONFLICT(asset_id, tag_id) DO NOTHING;
                """;
            linkCommand.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));
            linkCommand.Parameters.AddWithValue("$tag_id", tagId);
            linkCommand.Parameters.AddWithValue("$tagged_at", taggedAt.ToString("O"));
            assigned += await linkCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return assigned;
    }

    public async Task<int> RemoveAssetTagAsync(
        Guid tagId,
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        var ids = ValidateAssetTagIds(assetIds);
        if (ids.Length == 0)
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameters = ids
            .Select((_, index) => $"$asset_id_{index}")
            .ToArray();
        command.CommandText =
            $"""
             DELETE FROM asset_tag_links
             WHERE tag_id = $tag_id
               AND asset_id IN ({string.Join(", ", parameters)});
             """;
        command.Parameters.AddWithValue("$tag_id", tagId.ToString("D"));
        for (var index = 0; index < ids.Length; index++)
        {
            command.Parameters.AddWithValue(
                parameters[index],
                ids[index].ToString("D"));
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Guid[] ValidateAssetTagIds(IReadOnlyCollection<Guid> assetIds)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var ids = assetIds.Distinct().ToArray();
        if (ids.Length > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(assetIds));
        }

        return ids;
    }
}
