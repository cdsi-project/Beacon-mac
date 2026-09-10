using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.OpenWeb;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository : IOpenWebSettingsRepository
{
    public async Task<IReadOnlyList<OpenWebSource>> ListOpenWebSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, display_name, origin_domain, wordpress_username,
                   is_default, created_at, updated_at
            FROM openweb_sources
            ORDER BY is_default DESC, display_name COLLATE NOCASE, created_at;
            """;

        var sources = new List<OpenWebSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(ReadOpenWebSource(reader));
        }

        return sources;
    }

    public async Task SaveOpenWebSourceAsync(
        OpenWebSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        var makeDefault = source.IsDefault ||
            !await HasOpenWebSourcesAsync(connection, transaction, cancellationToken);
        if (makeDefault)
        {
            await SetAllOpenWebSourcesNonDefaultAsync(
                connection,
                transaction,
                cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO openweb_sources(
                id, display_name, origin_domain, wordpress_username,
                is_default, created_at, updated_at)
            VALUES(
                $id, $display_name, $origin_domain, $wordpress_username,
                $is_default, $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                origin_domain = excluded.origin_domain,
                wordpress_username = excluded.wordpress_username,
                is_default = excluded.is_default,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", source.Id.ToString());
        command.Parameters.AddWithValue("$display_name", source.DisplayName);
        command.Parameters.AddWithValue("$origin_domain", source.OriginDomain);
        command.Parameters.AddWithValue("$wordpress_username", source.WordPressUsername);
        command.Parameters.AddWithValue("$is_default", makeDefault ? 1 : 0);
        command.Parameters.AddWithValue("$created_at", source.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", source.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetDefaultOpenWebSourceAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText =
                "SELECT EXISTS(SELECT 1 FROM openweb_sources WHERE id = $id);";
            existsCommand.Parameters.AddWithValue("$id", sourceId.ToString());
            if (Convert.ToInt32(
                    await existsCommand.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                throw new InvalidOperationException("OpenWeb 源站不存在或已被删除。");
            }
        }

        await SetAllOpenWebSourcesNonDefaultAsync(
            connection,
            transaction,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE openweb_sources
            SET is_default = 1, updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", sourceId.ToString());
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteOpenWebSourceAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM openweb_sources WHERE id = $id;";
            command.Parameters.AddWithValue("$id", sourceId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE openweb_sources
                SET is_default = 1, updated_at = $updated_at
                WHERE id = (
                    SELECT id FROM openweb_sources
                    ORDER BY created_at, display_name COLLATE NOCASE
                    LIMIT 1)
                  AND NOT EXISTS(
                    SELECT 1 FROM openweb_sources WHERE is_default = 1);
                """;
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static OpenWebSource ReadOpenWebSource(SqliteDataReader reader)
    {
        return new OpenWebSource(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4) != 0,
            ParseTimestamp(reader.GetString(5)),
            ParseTimestamp(reader.GetString(6)));
    }

    private static async Task<bool> HasOpenWebSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM openweb_sources);";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task SetAllOpenWebSourcesNonDefaultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE openweb_sources SET is_default = 0;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
