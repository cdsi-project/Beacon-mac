using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Git;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository : IGitProfileRepository
{
    public async Task<IReadOnlyList<GitProfile>> ListGitProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, display_name, provider, repository_url, account_name,
                   default_branch, authentication_method, ssh_public_key_path,
                   is_default, created_at, updated_at
            FROM git_profiles
            ORDER BY is_default DESC, display_name COLLATE NOCASE, created_at;
            """;

        var profiles = new List<GitProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(ReadGitProfile(reader));
        }

        return profiles;
    }

    public async Task SaveGitProfileAsync(
        GitProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        var makeDefault = profile.IsDefault ||
            !await HasGitProfilesAsync(connection, transaction, cancellationToken);
        if (makeDefault)
        {
            await SetAllGitProfilesNonDefaultAsync(
                connection,
                transaction,
                cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO git_profiles(
                id, display_name, provider, repository_url, account_name,
                default_branch, authentication_method, ssh_public_key_path,
                is_default, created_at, updated_at)
            VALUES(
                $id, $display_name, $provider, $repository_url, $account_name,
                $default_branch, $authentication_method, $ssh_public_key_path,
                $is_default, $created_at, $updated_at)
            ON CONFLICT(id) DO UPDATE SET
                display_name = excluded.display_name,
                provider = excluded.provider,
                repository_url = excluded.repository_url,
                account_name = excluded.account_name,
                default_branch = excluded.default_branch,
                authentication_method = excluded.authentication_method,
                ssh_public_key_path = excluded.ssh_public_key_path,
                is_default = excluded.is_default,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$display_name", profile.DisplayName);
        command.Parameters.AddWithValue("$provider", profile.Provider.ToString());
        command.Parameters.AddWithValue("$repository_url", profile.RepositoryUrl);
        command.Parameters.AddWithValue("$account_name", profile.Username);
        command.Parameters.AddWithValue("$default_branch", profile.DefaultBranch);
        command.Parameters.AddWithValue(
            "$authentication_method",
            profile.AuthenticationMethod.ToString());
        command.Parameters.AddWithValue(
            "$ssh_public_key_path",
            (object?)profile.SshPublicKeyPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_default", makeDefault ? 1 : 0);
        command.Parameters.AddWithValue("$created_at", profile.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", profile.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetDefaultGitProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText =
                "SELECT EXISTS(SELECT 1 FROM git_profiles WHERE id = $id);";
            existsCommand.Parameters.AddWithValue("$id", profileId.ToString("D"));
            if (Convert.ToInt32(
                    await existsCommand.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                throw new InvalidOperationException("Git 配置不存在或已被删除。");
            }
        }

        await SetAllGitProfilesNonDefaultAsync(
            connection,
            transaction,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE git_profiles
            SET is_default = 1, updated_at = $updated_at
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteGitProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM git_profiles WHERE id = $id;";
            command.Parameters.AddWithValue("$id", profileId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE git_profiles
                SET is_default = 1, updated_at = $updated_at
                WHERE id = (
                    SELECT id FROM git_profiles
                    ORDER BY created_at, display_name COLLATE NOCASE
                    LIMIT 1)
                  AND NOT EXISTS(
                    SELECT 1 FROM git_profiles WHERE is_default = 1);
                """;
            command.Parameters.AddWithValue(
                "$updated_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static GitProfile ReadGitProfile(SqliteDataReader reader)
    {
        return new GitProfile(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            Enum.Parse<GitHostingProvider>(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(5),
            Enum.Parse<GitAuthenticationMethod>(reader.GetString(6)),
            reader.GetString(4),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8) != 0,
            ParseTimestamp(reader.GetString(9)),
            ParseTimestamp(reader.GetString(10)));
    }

    private static async Task<bool> HasGitProfilesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM git_profiles);";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task SetAllGitProfilesNonDefaultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE git_profiles SET is_default = 0;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
