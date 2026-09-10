using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.OpenWeb;

namespace CDSI.Agent.Application.OpenWeb;

public sealed class OpenWebSettingsService
{
    private const string LegacyApplicationPasswordSecretKey = "openweb-wordpress";
    private readonly IOpenWebSettingsRepository _repository;
    private readonly ISecretStore _secretStore;

    public OpenWebSettingsService(
        IOpenWebSettingsRepository repository,
        ISecretStore secretStore)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(secretStore);
        _repository = repository;
        _secretStore = secretStore;
    }

    public async Task<IReadOnlyList<ConfiguredOpenWebSource>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var sources = await _repository.ListOpenWebSourcesAsync(cancellationToken);
        await MigrateLegacySecretAsync(sources, cancellationToken);

        var configured = new List<ConfiguredOpenWebSource>(sources.Count);
        foreach (var source in sources)
        {
            configured.Add(new ConfiguredOpenWebSource(
                source,
                await _secretStore.ExistsAsync(
                    CreateSecretKey(source.Id),
                    cancellationToken)));
        }

        return configured;
    }

    public async Task<ConfiguredOpenWebSource> SaveAsync(
        SaveOpenWebSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sources = await _repository.ListOpenWebSourcesAsync(cancellationToken);
        await MigrateLegacySecretAsync(sources, cancellationToken);
        var existing = request.Id is null
            ? null
            : sources.SingleOrDefault(source => source.Id == request.Id.Value);
        if (request.Id is not null && existing is null)
        {
            throw new InvalidOperationException("OpenWeb 源站不存在或已被删除。");
        }

        if (!OpenWebOriginDomain.TryNormalize(
                request.OriginDomain,
                out var normalizedDomain,
                out var errorMessage) ||
            normalizedDomain is null)
        {
            throw new ArgumentException(
                errorMessage ?? "必须填写源站域名。",
                nameof(request));
        }

        if (sources.Any(source =>
                source.Id != request.Id &&
                string.Equals(
                    source.OriginDomain,
                    normalizedDomain,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "该 OpenWeb 源站域名已经配置。",
                nameof(request));
        }

        var id = existing?.Id ?? Guid.NewGuid();
        var secretKey = CreateSecretKey(id);
        var hasStoredPassword = await _secretStore.ExistsAsync(
            secretKey,
            cancellationToken);
        var password = NormalizeApplicationPassword(request.ApplicationPassword);
        if (password is null && !hasStoredPassword)
        {
            throw new ArgumentException(
                "首次配置时必须填写 WordPress 应用程序密码。",
                nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var source = new OpenWebSource(
            id,
            RequireValue(request.DisplayName, "源站名称", 100),
            normalizedDomain,
            NormalizeUsername(request.WordPressUsername),
            request.IsDefault || existing?.IsDefault == true || sources.Count == 0,
            existing?.CreatedAt ?? now,
            now);

        if (existing is null)
        {
            await _secretStore.StoreAsync(secretKey, password!, cancellationToken);
            try
            {
                await _repository.SaveOpenWebSourceAsync(source, cancellationToken);
            }
            catch (Exception saveException)
            {
                try
                {
                    await _secretStore.DeleteAsync(secretKey, CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    throw new InvalidOperationException(
                        "OpenWeb 源站保存失败，且临时凭据未能清理。",
                        new AggregateException(saveException, cleanupException));
                }

                throw;
            }

            return new ConfiguredOpenWebSource(source, true);
        }

        await _repository.SaveOpenWebSourceAsync(source, cancellationToken);
        if (password is not null)
        {
            await _secretStore.StoreAsync(secretKey, password, cancellationToken);
            return new ConfiguredOpenWebSource(source, true);
        }

        return new ConfiguredOpenWebSource(source, hasStoredPassword);
    }

    public Task SetDefaultAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        return _repository.SetDefaultOpenWebSourceAsync(sourceId, cancellationToken);
    }

    public async Task DeleteAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var secretKey = CreateSecretKey(sourceId);
        var previousPassword = await _secretStore.RetrieveAsync(
            secretKey,
            cancellationToken);
        await _secretStore.DeleteAsync(secretKey, cancellationToken);
        try
        {
            await _repository.DeleteOpenWebSourceAsync(sourceId, cancellationToken);
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

    public async Task<OpenWebConnection> GetConnectionAsync(
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var configured = (await ListAsync(cancellationToken))
            .SingleOrDefault(item => item.Source.Id == sourceId)
            ?? throw new InvalidOperationException("OpenWeb 源站不存在或已被删除。");
        if (!configured.HasApplicationPassword)
        {
            throw new InvalidOperationException(
                "OpenWeb 源站缺少 WordPress 应用程序密码，请在设置中重新保存凭据。");
        }

        var password = await _secretStore.RetrieveAsync(
            CreateSecretKey(sourceId),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "WordPress 应用程序密码不可用，请在 OpenWeb 设置中重新保存。");
        }

        return new OpenWebConnection(
            configured.Source.OriginDomain,
            configured.Source.WordPressUsername,
            password);
    }

    private async Task MigrateLegacySecretAsync(
        IReadOnlyList<OpenWebSource> sources,
        CancellationToken cancellationToken)
    {
        if (!sources.Any(source => source.Id == OpenWebSource.MigratedLegacySourceId))
        {
            return;
        }

        var newSecretKey = CreateSecretKey(OpenWebSource.MigratedLegacySourceId);
        if (await _secretStore.ExistsAsync(newSecretKey, cancellationToken) ||
            !await _secretStore.ExistsAsync(
                LegacyApplicationPasswordSecretKey,
                cancellationToken))
        {
            return;
        }

        var legacyPassword = await _secretStore.RetrieveAsync(
            LegacyApplicationPasswordSecretKey,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(legacyPassword))
        {
            return;
        }

        await _secretStore.StoreAsync(newSecretKey, legacyPassword, cancellationToken);
        await _secretStore.DeleteAsync(
            LegacyApplicationPasswordSecretKey,
            cancellationToken);
    }

    private static string NormalizeUsername(string? value)
    {
        var username = RequireValue(value, "WordPress 用户名", 100);
        if (username.Contains(':'))
        {
            throw new ArgumentException("WordPress 用户名不能包含冒号。", nameof(value));
        }

        return username;
    }

    private static string? NormalizeApplicationPassword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var password = string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
        if (password.Length > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "WordPress 应用程序密码过长。");
        }

        return password;
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

    private static string CreateSecretKey(Guid sourceId)
    {
        return $"openweb-wordpress-{sourceId:N}";
    }
}

public sealed record SaveOpenWebSourceRequest(
    Guid? Id,
    string DisplayName,
    string OriginDomain,
    string WordPressUsername,
    string? ApplicationPassword,
    bool IsDefault);

public sealed record ConfiguredOpenWebSource(
    OpenWebSource Source,
    bool HasApplicationPassword);
