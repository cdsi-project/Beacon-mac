using System.Net;
using Amazon.S3;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Infrastructure.Storage;

namespace CDSI.Agent.Infrastructure.Tests.Storage;

public sealed class S3CompatibleStorageAdapterTests
{
    [Theory]
    [InlineData(ObjectStorageProvider.QiniuKodo, true)]
    [InlineData(ObjectStorageProvider.TencentCos, false)]
    public void Constructor_ConfiguresProviderAndAddressingStyle(
        ObjectStorageProvider provider,
        bool forcePathStyle)
    {
        var adapter = new S3CompatibleStorageAdapter(provider);

        Assert.Equal(provider, adapter.Provider);
        Assert.Equal(forcePathStyle, adapter.ForcePathStyle);
    }

    [Fact]
    public void CreateServiceUrl_UsesTheConfiguredProtocol()
    {
        var profile = CreateProfile(useHttps: true);

        Assert.Equal(
            "https://s3.cn-east-1.qiniucs.com",
            S3CompatibleStorageAdapter.CreateServiceUrl(profile));
    }

    [Fact]
    public void MissingClassifiers_UnwrapNestedExceptions()
    {
        var missingObject = new IOException(
            "wrapped",
            new AmazonS3Exception("missing")
            {
                StatusCode = HttpStatusCode.NotFound,
                ErrorCode = "NoSuchKey"
            });
        var forbidden = new AmazonS3Exception("forbidden")
        {
            StatusCode = HttpStatusCode.Forbidden,
            ErrorCode = "AccessDenied"
        };

        Assert.True(S3CompatibleStorageAdapter.IsMissingObject(missingObject));
        Assert.True(S3CompatibleStorageAdapter.IsMissingUpload(missingObject));
        Assert.False(S3CompatibleStorageAdapter.IsMissingObject(forbidden));
        Assert.False(S3CompatibleStorageAdapter.IsMissingUpload(forbidden));
    }

    private static ObjectStorageProfile CreateProfile(bool useHttps)
    {
        var now = DateTimeOffset.UtcNow;
        return new ObjectStorageProfile(
            Guid.NewGuid(),
            "七牛备份",
            ObjectStorageProvider.QiniuKodo,
            "s3.cn-east-1.qiniucs.com",
            "cdsi-assets",
            "cn-east-1",
            useHttps,
            "access-key-id",
            now,
            now);
    }
}
