namespace CDSI.Agent.Core.Storage;

public sealed record ObjectStorageProfile(
    Guid Id,
    string DisplayName,
    ObjectStorageProvider Provider,
    string Endpoint,
    string BucketName,
    string? Region,
    bool UseHttps,
    string AccessKeyId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum ObjectStorageProvider
{
    AliyunOss,
    QiniuKodo,
    TencentCos
}
