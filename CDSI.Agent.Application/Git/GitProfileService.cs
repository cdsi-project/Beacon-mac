using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Git;

namespace CDSI.Agent.Application.Git;

public sealed class GitProfileService
{
    private readonly IGitProfileRepository _repository;
    private readonly ISecretStore _secretStore;

    public GitProfileService(
        IGitProfileRepository repository,
        ISecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(secretStore);
        _repository = repository;
        _secretStore = secretStore;
    }

    public async Task<IReadOnlyList<ConfiguredGitProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = await _repository.ListGitProfilesAsync(cancellationToken);
        await MigrateLegacySecretsAsync(profiles, cancellationToken);
        var configured = new List<ConfiguredGitProfile>(profiles.Count);
        foreach (var profile in profiles)
        {
            configured.Add(new ConfiguredGitProfile(
                profile,
                profile.AuthenticationMethod == GitAuthenticationMethod.Password &&
                await _secretStore.ExistsAsync(
                    CreatePasswordSecretKey(profile.Id),
                    cancellationToken)));
        }

        return configured;
    }

    public async Task<ConfiguredGitProfile> SaveAsync(
        SaveGitProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Provider))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "不支持该 Git 托管平台。");
        }

        if (!Enum.IsDefined(request.AuthenticationMethod))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "不支持该 Git 访问方式。");
        }

        var profiles = await _repository.ListGitProfilesAsync(cancellationToken);
        await MigrateLegacySecretsAsync(profiles, cancellationToken);
        var existing = request.Id is null
            ? null
            : profiles.SingleOrDefault(profile => profile.Id == request.Id.Value);
        if (request.Id is not null && existing is null)
        {
            throw new InvalidOperationException("Git 配置不存在或已被删除。");
        }

        if (!GitRepositoryAddress.TryNormalize(
                request.Provider,
                request.RepositoryUrl,
                out var repositoryUrl,
                out var errorMessage) ||
            repositoryUrl is null)
        {
            throw new ArgumentException(
                errorMessage ?? "Git 仓库地址无效。",
                nameof(request));
        }

        ValidateRepositoryAddressForAuthentication(
            request.AuthenticationMethod,
            repositoryUrl);
        if (profiles.Any(profile =>
                profile.Id != request.Id &&
                profile.Provider == request.Provider &&
                string.Equals(
                    profile.RepositoryUrl,
                    repositoryUrl,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("该 Git 仓库已经配置。", nameof(request));
        }

        var id = existing?.Id ?? Guid.NewGuid();
        var password = NormalizePassword(request.Password);
        var passwordSecretKey = CreatePasswordSecretKey(id);
        var hasStoredPassword = await _secretStore.ExistsAsync(
            passwordSecretKey,
            cancellationToken);
        if (request.AuthenticationMethod == GitAuthenticationMethod.Password &&
            password is null &&
            !hasStoredPassword)
        {
            throw new ArgumentException(
                "首次使用密码方式或从 SSH 切换到密码方式时必须填写密码。",
                nameof(request));
        }

        var username = request.AuthenticationMethod == GitAuthenticationMethod.Password
            ? RequireValue(request.Username, "用户名", 100)
            : string.Empty;
        var sshPublicKeyPath = request.AuthenticationMethod == GitAuthenticationMethod.Ssh
            ? NormalizeSshPublicKeyPath(request.SshPublicKeyPath)
            : null;
        var now = DateTimeOffset.UtcNow;
        var profile = new GitProfile(
            id,
            RequireValue(request.DisplayName, "配置名称", 100),
            request.Provider,
            repositoryUrl,
            NormalizeBranch(request.DefaultBranch),
            request.AuthenticationMethod,
            username,
            sshPublicKeyPath,
            request.IsDefault || existing?.IsDefault == true || profiles.Count == 0,
            existing?.CreatedAt ?? now,
            now);

        if (profile.AuthenticationMethod == GitAuthenticationMethod.Password)
        {
            await SavePasswordProfileAsync(
                profile,
                password,
                passwordSecretKey,
                cancellationToken);
            return new ConfiguredGitProfile(profile, true);
        }

        await SaveSshProfileAsync(
            profile,
            passwordSecretKey,
            cancellationToken);
        return new ConfiguredGitProfile(profile, false);
    }

    public Task SetDefaultAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        return _repository.SetDefaultGitProfileAsync(profileId, cancellationToken);
    }

    public async Task DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profiles = await _repository.ListGitProfilesAsync(cancellationToken);
        await MigrateLegacySecretsAsync(profiles, cancellationToken);
        var secretKey = CreatePasswordSecretKey(profileId);
        var previousPassword = await _secretStore.RetrieveAsync(
            secretKey,
            cancellationToken);
        try
        {
            await _secretStore.DeleteAsync(secretKey, cancellationToken);
            await _secretStore.DeleteAsync(
                CreateLegacySecretKey(profileId),
                cancellationToken);
            await _repository.DeleteGitProfileAsync(profileId, cancellationToken);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(previousPassword))
            {
                await _secretStore.StoreAsync(
                    secretKey,
                    previousPassword,
                    CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<string?> GetPasswordAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profiles = await _repository.ListGitProfilesAsync(cancellationToken);
        await MigrateLegacySecretsAsync(profiles, cancellationToken);
        var profile = profiles.SingleOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException("Git 配置不存在或已被删除。");
        if (profile.AuthenticationMethod != GitAuthenticationMethod.Password)
        {
            return null;
        }

        return await _secretStore.RetrieveAsync(
            CreatePasswordSecretKey(profileId),
            cancellationToken);
    }

    private async Task SavePasswordProfileAsync(
        GitProfile profile,
        string? password,
        string secretKey,
        CancellationToken cancellationToken)
    {
        if (password is null)
        {
            await _repository.SaveGitProfileAsync(profile, cancellationToken);
            return;
        }

        var previousPassword = await _secretStore.RetrieveAsync(
            secretKey,
            cancellationToken);
        await _secretStore.StoreAsync(secretKey, password, cancellationToken);
        try
        {
            await _repository.SaveGitProfileAsync(profile, cancellationToken);
        }
        catch
        {
            if (previousPassword is null)
            {
                await _secretStore.DeleteAsync(secretKey, CancellationToken.None);
            }
            else
            {
                await _secretStore.StoreAsync(
                    secretKey,
                    previousPassword,
                    CancellationToken.None);
            }

            throw;
        }
    }

    private async Task SaveSshProfileAsync(
        GitProfile profile,
        string secretKey,
        CancellationToken cancellationToken)
    {
        var previousPassword = await _secretStore.RetrieveAsync(
            secretKey,
            cancellationToken);
        try
        {
            await _secretStore.DeleteAsync(secretKey, cancellationToken);
            await _secretStore.DeleteAsync(
                CreateLegacySecretKey(profile.Id),
                cancellationToken);
            await _repository.SaveGitProfileAsync(profile, cancellationToken);
        }
        catch
        {
            if (previousPassword is not null)
            {
                await _secretStore.StoreAsync(
                    secretKey,
                    previousPassword,
                    CancellationToken.None);
            }

            throw;
        }
    }

    private async Task MigrateLegacySecretsAsync(
        IReadOnlyList<GitProfile> profiles,
        CancellationToken cancellationToken)
    {
        foreach (var profile in profiles)
        {
            var legacyKey = CreateLegacySecretKey(profile.Id);
            if (profile.AuthenticationMethod == GitAuthenticationMethod.Ssh)
            {
                await _secretStore.DeleteAsync(legacyKey, cancellationToken);
                await _secretStore.DeleteAsync(
                    CreatePasswordSecretKey(profile.Id),
                    cancellationToken);
                continue;
            }

            if (!await _secretStore.ExistsAsync(legacyKey, cancellationToken))
            {
                continue;
            }

            var passwordKey = CreatePasswordSecretKey(profile.Id);
            if (!await _secretStore.ExistsAsync(passwordKey, cancellationToken))
            {
                var legacySecret = await _secretStore.RetrieveAsync(
                    legacyKey,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(legacySecret))
                {
                    await _secretStore.StoreAsync(
                        passwordKey,
                        legacySecret,
                        cancellationToken);
                }
            }

            await _secretStore.DeleteAsync(legacyKey, cancellationToken);
        }
    }

    private static void ValidateRepositoryAddressForAuthentication(
        GitAuthenticationMethod authenticationMethod,
        string repositoryUrl)
    {
        var isSsh = repositoryUrl.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
            repositoryUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);
        if (authenticationMethod == GitAuthenticationMethod.Password && isSsh)
        {
            throw new ArgumentException("密码访问方式必须使用 HTTPS 仓库地址。");
        }

        if (authenticationMethod == GitAuthenticationMethod.Ssh && !isSsh)
        {
            throw new ArgumentException("SSH 访问方式必须使用 SSH 仓库地址。");
        }
    }

    private static string NormalizeBranch(string? value)
    {
        var branch = RequireValue(value, "默认分支", 255);
        if (branch.Any(char.IsWhiteSpace) ||
            branch.StartsWith('.') ||
            branch.EndsWith('.') ||
            branch.StartsWith('/') ||
            branch.EndsWith('/') ||
            branch.Contains("..", StringComparison.Ordinal) ||
            branch.IndexOfAny(['~', '^', ':', '?', '*', '[', '\\']) >= 0)
        {
            throw new ArgumentException("默认分支名称无效。", nameof(value));
        }

        return branch;
    }

    private static string? NormalizePassword(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
        {
            throw new ArgumentException("密码格式无效。", nameof(value));
        }

        return value;
    }

    private static string NormalizeSshPublicKeyPath(string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var publicKeyPath = Path.GetFullPath(value.Trim());
        if (!publicKeyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(publicKeyPath))
        {
            throw new ArgumentException("请选择存在的 SSH 公钥文件（.pub）。", nameof(value));
        }

        var privateKeyPath = publicKeyPath[..^4];
        if (!File.Exists(privateKeyPath))
        {
            throw new ArgumentException(
                "所选公钥缺少对应的私钥文件，Beacon 不会读取该私钥。",
                nameof(value));
        }

        return publicKeyPath;
    }

    private static string RequireValue(
        string? value,
        string fieldName,
        int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                fieldName,
                $"{fieldName}最多允许 {maximumLength} 个字符。");
        }

        return normalized;
    }

    private static string CreatePasswordSecretKey(Guid profileId)
    {
        return $"git-password-{profileId:N}";
    }

    private static string CreateLegacySecretKey(Guid profileId)
    {
        return $"git-access-token-{profileId:N}";
    }
}

public sealed record SaveGitProfileRequest(
    Guid? Id,
    string DisplayName,
    GitHostingProvider Provider,
    string RepositoryUrl,
    string DefaultBranch,
    GitAuthenticationMethod AuthenticationMethod,
    string? Username,
    string? Password,
    string? SshPublicKeyPath,
    bool IsDefault);

public sealed record ConfiguredGitProfile(
    GitProfile Profile,
    bool HasPassword);
