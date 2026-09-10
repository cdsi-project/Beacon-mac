using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository : IStorageProfileRepository
{
    public async Task<IReadOnlyList<ObjectStorageProfile>> ListStorageProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = new List<ObjectStorageProfile>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, display_name, provider, endpoint, bucket_name,
                region, use_https, access_key_id, created_at, updated_at
            FROM storage_profiles
            ORDER BY display_name, bucket_name;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(ReadStorageProfile(reader));
        }

        return profiles;
    }

    public async Task SaveStorageProfileAsync(
        ObjectStorageProfile profile,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO storage_profiles(
                id, display_name, provider, endpoint, bucket_name,
                region, use_https, access_key_id, created_at, updated_at)
            VALUES(
                $id, $display_name, $provider, $endpoint, $bucket_name,
                $region, $use_https, $access_key_id, $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                provider = excluded.provider,
                endpoint = excluded.endpoint,
                bucket_name = excluded.bucket_name,
                region = excluded.region,
                use_https = excluded.use_https,
                access_key_id = excluded.access_key_id,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$display_name", profile.DisplayName);
        command.Parameters.AddWithValue("$provider", profile.Provider.ToString());
        command.Parameters.AddWithValue("$endpoint", profile.Endpoint);
        command.Parameters.AddWithValue("$bucket_name", profile.BucketName);
        command.Parameters.AddWithValue(
            "$region",
            (object?)profile.Region ?? DBNull.Value);
        command.Parameters.AddWithValue("$use_https", profile.UseHttps ? 1 : 0);
        command.Parameters.AddWithValue("$access_key_id", profile.AccessKeyId);
        command.Parameters.AddWithValue("$created_at", profile.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", profile.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteStorageProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM storage_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static ObjectStorageProfile ReadStorageProfile(SqliteDataReader reader)
    {
        return new ObjectStorageProfile(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Enum.Parse<ObjectStorageProvider>(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6) != 0,
            reader.GetString(7),
            ParseTimestamp(reader.GetString(8)),
            ParseTimestamp(reader.GetString(9)));
    }
}
