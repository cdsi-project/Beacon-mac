using CDSI.Agent.Application.Collections;
using CDSI.Agent.Application.Workspaces;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.Git;

namespace CDSI.Agent.Application.Git;

public sealed class GitProjectSyncService
{
    private const long LargeFileWarningThreshold = 50L * 1024 * 1024;
    private readonly AssetCollectionService _collectionService;
    private readonly GitProfileService _profileService;
    private readonly WorkspaceApplicationService _workspaceService;
    private readonly IGitProjectSynchronizer _synchronizer;
    private readonly IGitProjectSyncRepository _syncRepository;

    public GitProjectSyncService(
        AssetCollectionService collectionService,
        GitProfileService profileService,
        WorkspaceApplicationService workspaceService,
        IGitProjectSynchronizer synchronizer,
        IGitProjectSyncRepository syncRepository)
    {
        ArgumentNullException.ThrowIfNull(collectionService);
        ArgumentNullException.ThrowIfNull(profileService);
        ArgumentNullException.ThrowIfNull(workspaceService);
        ArgumentNullException.ThrowIfNull(synchronizer);
        ArgumentNullException.ThrowIfNull(syncRepository);
        _collectionService = collectionService;
        _profileService = profileService;
        _workspaceService = workspaceService;
        _synchronizer = synchronizer;
        _syncRepository = syncRepository;
    }

    public async Task<GitProjectSyncPreview> PrepareAsync(
        Guid projectId,
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var context = await BuildContextAsync(
            projectId,
            profileId,
            includePassword: false,
            cancellationToken);
        return new GitProjectSyncPreview(
            context.Project.Id,
            context.Project.Name,
            context.Profile.Id,
            context.Profile.DisplayName,
            context.Profile.RepositoryUrl,
            context.Profile.DefaultBranch,
            context.Assets.Count,
            context.Assets.Sum(asset => asset.Size),
            context.Assets.Count(asset => asset.Size >= LargeFileWarningThreshold));
    }

    public async Task<GitProjectSyncResult> SyncAsync(
        Guid projectId,
        Guid profileId,
        IProgress<GitProjectSyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var context = await BuildContextAsync(
            projectId,
            profileId,
            includePassword: true,
            cancellationToken);
        var result = await _synchronizer.SyncAsync(
            new GitProjectSyncRequest(
                context.Profile,
                context.Password,
                context.WorkspacePath,
                context.Project,
                context.Assets),
            progress,
            cancellationToken);
        try
        {
            await _syncRepository.SaveGitProjectSyncAsync(
                new GitProjectSyncRecord(
                    context.Project.Id,
                    context.Project.Name,
                    context.Project.Type,
                    context.Profile.Id,
                    context.Profile.DisplayName,
                    context.Profile.Provider,
                    context.Profile.RepositoryUrl,
                    result.Branch,
                    result.CommitId,
                    result.SyncedFiles,
                    result.SyncedBytes,
                    result.CreatedCommit,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Git 推送已完成，但本地同步记录保存失败。远端仓库可能已经更新，请勿立即重复同步。",
                exception);
        }

        return result;
    }

    public Task<IReadOnlyList<GitProjectSyncRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        return _syncRepository.ListGitProjectSyncsAsync(cancellationToken);
    }

    public static bool IsProfileReady(ConfiguredGitProfile configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        var profile = configured.Profile;
        if (profile.AuthenticationMethod == GitAuthenticationMethod.Password)
        {
            return configured.HasPassword;
        }

        var publicKeyPath = profile.SshPublicKeyPath;
        return !string.IsNullOrWhiteSpace(publicKeyPath) &&
            publicKeyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(publicKeyPath) &&
            File.Exists(publicKeyPath[..^4]);
    }

    internal static void ValidateAssets(IReadOnlyList<AssetListItem> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (assets.Count == 0)
        {
            throw new InvalidOperationException("项目中没有可同步的资产。");
        }

        var unavailable = assets.Count(asset =>
            asset.LocationStatus != AssetLocationStatus.Available ||
            !File.Exists(asset.Path));
        if (unavailable > 0)
        {
            throw new InvalidOperationException(
                $"项目中有 {unavailable:N0} 个本地文件不可用，无法同步到 Git。");
        }

        var invalidFilename = assets.FirstOrDefault(asset =>
            !string.Equals(
                Path.GetFileName(asset.OriginalFilename),
                asset.OriginalFilename,
                StringComparison.Ordinal) ||
            string.Equals(asset.OriginalFilename, ".git", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                asset.OriginalFilename,
                GitProjectSyncConventions.ManifestFileName,
                StringComparison.OrdinalIgnoreCase));
        if (invalidFilename is not null)
        {
            throw new InvalidOperationException(
                $"文件名“{invalidFilename.OriginalFilename}”不能同步到 Git 仓库根目录。");
        }

        var duplicateFilename = assets
            .GroupBy(asset => asset.OriginalFilename, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateFilename is not null)
        {
            throw new InvalidOperationException(
                $"项目中存在多个同名文件“{duplicateFilename}”，请调整后再同步到 Git。");
        }
    }

    private async Task<GitProjectSyncContext> BuildContextAsync(
        Guid projectId,
        Guid profileId,
        bool includePassword,
        CancellationToken cancellationToken)
    {
        var profiles = await _profileService.ListAsync(cancellationToken);
        var configured = profiles.SingleOrDefault(item => item.Profile.Id == profileId)
            ?? throw new InvalidOperationException("Git 配置不存在或已被删除。");
        if (!IsProfileReady(configured))
        {
            throw new InvalidOperationException(
                "所选 Git 配置缺少有效凭据或 SSH 密钥，请先在设置中修复。");
        }

        var workspace = await _workspaceService.GetAsync(cancellationToken)
            ?? throw new InvalidOperationException("尚未配置 CDSI 工作目录。");
        var plan = await _collectionService.PrepareSyncAsync(projectId, cancellationToken);
        var assets = plan.Assets
            .GroupBy(asset => asset.AssetId)
            .Select(group => group.First())
            .ToArray();
        ValidateAssets(assets);

        string? password = null;
        if (includePassword &&
            configured.Profile.AuthenticationMethod == GitAuthenticationMethod.Password)
        {
            password = await _profileService.GetPasswordAsync(profileId, cancellationToken);
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    "所选 Git 配置的密码或访问令牌不存在，请先在设置中重新填写。");
            }
        }

        return new GitProjectSyncContext(
            plan.Collection,
            assets,
            configured.Profile,
            password,
            workspace.Path);
    }

    private sealed record GitProjectSyncContext(
        AssetCollection Project,
        IReadOnlyList<AssetListItem> Assets,
        GitProfile Profile,
        string? Password,
        string WorkspacePath);
}

public sealed record GitProjectSyncPreview(
    Guid ProjectId,
    string ProjectName,
    Guid ProfileId,
    string ProfileName,
    string RepositoryUrl,
    string Branch,
    int AssetCount,
    long TotalBytes,
    int LargeFileCount);
