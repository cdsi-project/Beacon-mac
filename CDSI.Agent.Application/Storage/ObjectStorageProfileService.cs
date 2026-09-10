using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Application.Storage;

public sealed class ObjectStorageProfileService
{
    private readonly IStorageProfileRepository _repository;
    private readonly ISecretStore _secretStore;

    public ObjectStorageProfileService(
        IStorageProfileRepository repository,
        ISecretStore secretStore)
    {
        _repository = repository;
        _secretStore = secretStore;
    }

    public async Task<IReadOnlyList<ConfiguredObjectStorageProfile>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = await _repository.ListStorageProfilesAsync(cancellationToken);
        var configured = new List<ConfiguredObjectStorageProfile>(profiles.Count);
        foreach (var profile in profiles)
        {
            configured.Add(new ConfiguredObjectStorageProfile(
                profile,
                await _secretStore.ExistsAsync(
                    CreateSecretKey(profile.Id),
                    cancellationToken)));
        }

        return configured;
    }

    public async Task<ObjectStorageConnection> GetConnectionAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var profile = (await _repository.ListStorageProfilesAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == profileId)
            ?? throw new InvalidOperationException("备份配置不存在或已被删除。");
        var secret = await _secretStore.RetrieveAsync(
            CreateSecretKey(profileId),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                "备份配置缺少 AccessKey Secret，请在设置中重新保存凭据。");
        }

        return new ObjectStorageConnection(profile, secret);
    }

    public async Task<ConfiguredObjectStorageProfile> SaveAsync(
        SaveObjectStorageProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profiles = await _repository.ListStorageProfilesAsync(cancellationToken);
        var existing = request.Id is null
            ? null
            : profiles.SingleOrDefault(profile => profile.Id == request.Id.Value);
        if (request.Id is not null && existing is null)
        {
            throw new InvalidOperationException("备份配置不存在或已被删除。");
        }

        if (!Enum.IsDefined(request.Provider))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Provider),
                "不支持所选备份提供商。");
        }

        var id = existing?.Id ?? Guid.NewGuid();
        var secretKey = CreateSecretKey(id);
        var hasStoredSecret = await _secretStore.ExistsAsync(
            secretKey,
            cancellationToken);
        var accessKeySecret = request.AccessKeySecret;
        var providerChanged = existing is not null &&
            existing.Provider != request.Provider;
        if (string.IsNullOrWhiteSpace(accessKeySecret) &&
            (!hasStoredSecret || providerChanged))
        {
            throw new ArgumentException(providerChanged
                ? "更换备份提供商时必须重新填写 AccessKey Secret。"
                : "首次保存时必须填写 AccessKey Secret。");
        }

        var region = NormalizeOptional(request.Region, 100, "地域");
        if ((request.Provider is ObjectStorageProvider.QiniuKodo or
                ObjectStorageProvider.TencentCos) &&
            region is null)
        {
            throw new ArgumentException(
                $"{FormatProvider(request.Provider)} 配置必须填写 Region ID。");
        }

        var now = DateTimeOffset.UtcNow;
        var profile = new ObjectStorageProfile(
            id,
            RequireValue(request.DisplayName, "配置名称", 100),
            request.Provider,
            NormalizeEndpoint(request.Endpoint, request.UseHttps),
            ValidateBucketName(request.BucketName),
            region,
            request.UseHttps,
            RequireValue(request.AccessKeyId, "AccessKey ID", 128),
            existing?.CreatedAt ?? now,
            now);

        if (existing is null)
        {
            await _secretStore.StoreAsync(
                secretKey,
                accessKeySecret!,
                cancellationToken);
            try
            {
                await _repository.SaveStorageProfileAsync(profile, cancellationToken);
            }
            catch (Exception saveException)
            {
                try
                {
                    await _secretStore.DeleteAsync(
                        secretKey,
                        CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    throw new InvalidOperationException(
                        "备份配置保存失败，且临时凭据未能清理。",
                        new AggregateException(saveException, cleanupException));
                }

                throw;
            }

            return new ConfiguredObjectStorageProfile(profile, true);
        }

        await _repository.SaveStorageProfileAsync(profile, cancellationToken);
        if (!string.IsNullOrWhiteSpace(accessKeySecret))
        {
            await _secretStore.StoreAsync(
                secretKey,
                accessKeySecret,
                cancellationToken);
            return new ConfiguredObjectStorageProfile(profile, true);
        }

        return new ConfiguredObjectStorageProfile(profile, hasStoredSecret);
    }

    public async Task DeleteAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var secretKey = CreateSecretKey(profileId);
        await _secretStore.DeleteAsync(secretKey, cancellationToken);
        await _repository.DeleteStorageProfileAsync(profileId, cancellationToken);
    }

    private static string NormalizeEndpoint(string value, bool useHttps)
    {
        var endpoint = RequireValue(value, "Endpoint", 255);
        var hasScheme = endpoint.Contains("://", StringComparison.Ordinal);
        var candidate = hasScheme
            ? endpoint
            : $"{(useHttps ? "https" : "http")}://{endpoint}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Endpoint 必须是有效主机名，不能包含路径、查询或账号信息。");
        }

        if (hasScheme &&
            (uri.Scheme == Uri.UriSchemeHttps) != useHttps)
        {
            throw new ArgumentException("Endpoint 协议与 HTTPS 选项不一致。");
        }

        return uri.IsDefaultPort ? uri.Host : uri.Authority;
    }

    private static string ValidateBucketName(string value)
    {
        var bucket = RequireValue(value, "Bucket", 63);
        if (bucket.Length < 3 ||
            !char.IsAsciiLetterOrDigit(bucket[0]) ||
            !char.IsAsciiLetterOrDigit(bucket[^1]) ||
            bucket.Any(character =>
                !(character is >= 'a' and <= 'z') &&
                !char.IsAsciiDigit(character) &&
                character != '-'))
        {
            throw new ArgumentException(
                "Bucket 必须为 3-63 个小写字母、数字或短划线，并以字母或数字开头和结尾。");
        }

        return bucket;
    }

    private static string RequireValue(
        string value,
        string fieldName,
        int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                fieldName,
                $"{fieldName} 最多允许 {maximumLength} 个字符。");
        }

        return normalized;
    }

    private static string? NormalizeOptional(
        string? value,
        int maximumLength,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return RequireValue(value, fieldName, maximumLength);
    }

    private static string CreateSecretKey(Guid profileId)
    {
        return $"oss-{profileId:N}";
    }

    private static string FormatProvider(ObjectStorageProvider provider)
    {
        return provider switch
        {
            ObjectStorageProvider.AliyunOss => "阿里云 OSS",
            ObjectStorageProvider.QiniuKodo => "七牛云 Kodo",
            ObjectStorageProvider.TencentCos => "腾讯云 COS",
            _ => provider.ToString()
        };
    }
}

public sealed record SaveObjectStorageProfileRequest(
    Guid? Id,
    string DisplayName,
    string Endpoint,
    string BucketName,
    string? Region,
    bool UseHttps,
    string AccessKeyId,
    string? AccessKeySecret,
    ObjectStorageProvider Provider = ObjectStorageProvider.AliyunOss);

public sealed record ConfiguredObjectStorageProfile(
    ObjectStorageProfile Profile,
    bool HasStoredSecret);
