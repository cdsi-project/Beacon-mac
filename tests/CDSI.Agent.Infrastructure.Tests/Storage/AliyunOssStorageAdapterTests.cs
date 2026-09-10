using AlibabaCloud.OSS.V2;
using CDSI.Agent.Infrastructure.Storage;

namespace CDSI.Agent.Infrastructure.Tests.Storage;

public sealed class AliyunOssStorageAdapterTests
{
    [Fact]
    public void IsMissingObject_UnwrapsTheSdkOperationException()
    {
        var exception = new OperationException(
            "HeadObject",
            CreateServiceException(404));

        Assert.True(AliyunOssStorageAdapter.IsMissingObject(exception));
    }

    [Fact]
    public void IsMissingUpload_UnwrapsNestedAndAggregateExceptions()
    {
        var exception = new AggregateException(
            new IOException(
                "wrapped",
                new OperationException(
                    "ListParts",
                    CreateServiceException(404))));

        Assert.True(AliyunOssStorageAdapter.IsMissingUpload(exception));
    }

    [Fact]
    public void MissingClassifiers_DoNotSwallowAuthorizationFailures()
    {
        var exception = new OperationException(
            "HeadObject",
            CreateServiceException(403));

        Assert.False(AliyunOssStorageAdapter.IsMissingObject(exception));
        Assert.False(AliyunOssStorageAdapter.IsMissingUpload(exception));
    }

    private static ServiceException CreateServiceException(int statusCode)
    {
        return new ServiceException(
            statusCode,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
    }
}
