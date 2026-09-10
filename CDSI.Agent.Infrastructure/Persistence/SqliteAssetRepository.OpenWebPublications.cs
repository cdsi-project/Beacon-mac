using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.OpenWeb;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository : IOpenWebPublicationRepository
{
    public async Task<OpenWebPublication?> GetOpenWebPublicationAsync(
        Guid assetId,
        OpenWebPublisher publisher,
        string originDomain,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originDomain);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT remote_post_id, remote_url, remote_status,
                   content_sha256, synchronized_at
            FROM openweb_publications
            WHERE asset_id = $asset_id
              AND publisher = $publisher
              AND origin_domain = $origin_domain;
            """;
        command.Parameters.AddWithValue("$asset_id", assetId.ToString("D"));
        command.Parameters.AddWithValue("$publisher", publisher.ToString());
        command.Parameters.AddWithValue("$origin_domain", originDomain);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OpenWebPublication(
            assetId,
            publisher,
            originDomain,
            reader.GetInt64(0),
            reader.GetString(1),
            Enum.Parse<OpenWebArticleStatus>(reader.GetString(2)),
            reader.GetString(3),
            ParseTimestamp(reader.GetString(4)));
    }

    public async Task SaveOpenWebPublicationAsync(
        OpenWebPublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO openweb_publications(
                asset_id, publisher, origin_domain, remote_post_id, remote_url,
                remote_status, content_sha256, synchronized_at)
            VALUES(
                $asset_id, $publisher, $origin_domain, $remote_post_id, $remote_url,
                $remote_status, $content_sha256, $synchronized_at)
            ON CONFLICT(asset_id, publisher, origin_domain) DO UPDATE SET
                remote_post_id = excluded.remote_post_id,
                remote_url = excluded.remote_url,
                remote_status = excluded.remote_status,
                content_sha256 = excluded.content_sha256,
                synchronized_at = excluded.synchronized_at;
            """;
        command.Parameters.AddWithValue(
            "$asset_id",
            publication.AssetId.ToString("D"));
        command.Parameters.AddWithValue(
            "$publisher",
            publication.Publisher.ToString());
        command.Parameters.AddWithValue(
            "$origin_domain",
            publication.OriginDomain);
        command.Parameters.AddWithValue(
            "$remote_post_id",
            publication.RemotePostId);
        command.Parameters.AddWithValue("$remote_url", publication.RemoteUrl);
        command.Parameters.AddWithValue(
            "$remote_status",
            publication.Status.ToString());
        command.Parameters.AddWithValue(
            "$content_sha256",
            publication.ContentSha256);
        command.Parameters.AddWithValue(
            "$synchronized_at",
            publication.SynchronizedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
