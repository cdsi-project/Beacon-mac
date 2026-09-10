using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Git;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.Infrastructure.Persistence;

public sealed partial class SqliteAssetRepository : IGitProjectSyncRepository
{
    public async Task<IReadOnlyList<GitProjectSyncRecord>> ListGitProjectSyncsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT project_id, project_name, project_type,
                   profile_id, profile_name, provider, repository_url,
                   branch, commit_id, synced_files, synced_bytes,
                   created_commit, synced_at
            FROM git_project_syncs
            ORDER BY synced_at DESC, project_name COLLATE NOCASE,
                     profile_name COLLATE NOCASE;
            """;

        var records = new List<GitProjectSyncRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new GitProjectSyncRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                Enum.Parse<AssetCollectionType>(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                Enum.Parse<GitHostingProvider>(reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetInt64(10),
                reader.GetInt32(11) != 0,
                DateTimeOffset.Parse(
                    reader.GetString(12),
                    System.Globalization.CultureInfo.InvariantCulture)));
        }

        return records;
    }

    public async Task SaveGitProjectSyncAsync(
        GitProjectSyncRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO git_project_syncs(
                project_id, profile_id, project_name, project_type,
                profile_name, provider, repository_url, branch, commit_id,
                synced_files, synced_bytes, created_commit, synced_at)
            VALUES(
                $project_id, $profile_id, $project_name, $project_type,
                $profile_name, $provider, $repository_url, $branch, $commit_id,
                $synced_files, $synced_bytes, $created_commit, $synced_at)
            ON CONFLICT(project_id, profile_id) DO UPDATE SET
                project_name = excluded.project_name,
                project_type = excluded.project_type,
                profile_name = excluded.profile_name,
                provider = excluded.provider,
                repository_url = excluded.repository_url,
                branch = excluded.branch,
                commit_id = excluded.commit_id,
                synced_files = excluded.synced_files,
                synced_bytes = excluded.synced_bytes,
                created_commit = excluded.created_commit,
                synced_at = excluded.synced_at;
            """;
        command.Parameters.AddWithValue("$project_id", record.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$profile_id", record.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$project_name", record.ProjectName);
        command.Parameters.AddWithValue("$project_type", record.ProjectType.ToString());
        command.Parameters.AddWithValue("$profile_name", record.ProfileName);
        command.Parameters.AddWithValue("$provider", record.Provider.ToString());
        command.Parameters.AddWithValue("$repository_url", record.RepositoryUrl);
        command.Parameters.AddWithValue("$branch", record.Branch);
        command.Parameters.AddWithValue("$commit_id", record.CommitId);
        command.Parameters.AddWithValue("$synced_files", record.SyncedFiles);
        command.Parameters.AddWithValue("$synced_bytes", record.SyncedBytes);
        command.Parameters.AddWithValue("$created_commit", record.CreatedCommit ? 1 : 0);
        command.Parameters.AddWithValue("$synced_at", record.SyncedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
