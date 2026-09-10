using System.Globalization;
using AlibabaCloud.OSS.V2;
using AlibabaCloud.OSS.V2.Credentials;
using AlibabaCloud.OSS.V2.IO;
using AlibabaCloud.OSS.V2.Models;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Storage;
using OSS = AlibabaCloud.OSS.V2;

namespace CDSI.Agent.Infrastructure.Storage;

public sealed class AliyunOssStorageAdapter : IObjectStorageAdapter
{
    private const long DefaultPartSize = 16L * 1024 * 1024;
    private const long SingleCopyMaximumSize = 1024L * 1024 * 1024;
    private const long MaximumPartCount = 10_000;
    private const string Sha256MetadataKey = "cdsi-sha256";
    private const string AssetIdMetadataKey = "cdsi-asset-id";

    public ObjectStorageProvider Provider => ObjectStorageProvider.AliyunOss;

    public async Task<ObjectStorageObjectInfo?> StatAsync(
        ObjectStorageConnection connection,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        using var client = CreateClient(connection);
        return await StatWithClientAsync(
            client,
            connection.Profile.BucketName,
            objectKey,
            cancellationToken);
    }

    public async Task<ObjectStorageTransferResult> UploadAsync(
        ObjectStorageTransferRequest request,
        Func<MultipartUploadSession, CancellationToken, Task> saveCheckpoint,
        IProgress<ObjectStorageTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(saveCheckpoint);
        var sourceSnapshot = ReadAndValidateSource(request);
        using var client = CreateClient(request.Connection);

        if (request.Size < DefaultPartSize)
        {
            await UploadSingleObjectAsync(
                client,
                request,
                progress,
                cancellationToken);
        }
        else
        {
            await UploadMultipartAsync(
                client,
                request,
                saveCheckpoint,
                progress,
                cancellationToken);
        }

        EnsureSourceUnchanged(request, sourceSnapshot);
        var uploaded = await StatWithClientAsync(
            client,
            request.Connection.Profile.BucketName,
            request.ObjectKey,
            cancellationToken)
            ?? throw new IOException("OSS 上传完成后未找到目标对象。");
        return new ObjectStorageTransferResult(uploaded, Uploaded: true);
    }

    public async Task<ObjectStorageDownloadResult> DownloadAsync(
        ObjectStorageConnection connection,
        string objectKey,
        Stream destination,
        IProgress<ObjectStorageDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("下载目标流不可写。", nameof(destination));
        }

        using var client = CreateClient(connection);
        var result = await client.GetObjectAsync(
            new GetObjectRequest
            {
                Bucket = connection.Profile.BucketName,
                Key = objectKey,
                ProgressFn = (_, transferred, total) =>
                    progress?.Report(new ObjectStorageDownloadProgress(
                        transferred,
                        total,
                        "正在下载"))
            },
            cancellationToken: cancellationToken);
        if (result.Body is null)
        {
            throw new IOException("OSS 下载响应缺少对象数据流。");
        }

        await using (result.Body)
        {
            await result.Body.CopyToAsync(
                destination,
                1024 * 1024,
                cancellationToken);
        }

        var size = result.ContentLength
            ?? throw new IOException("OSS 下载响应缺少 Content-Length。");
        DateTimeOffset? lastModified = null;
        if (DateTimeOffset.TryParse(
                result.LastModified,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            lastModified = parsed;
        }

        progress?.Report(new ObjectStorageDownloadProgress(
            size,
            size,
            "下载完成"));
        return new ObjectStorageDownloadResult(
            new ObjectStorageObjectInfo(
                objectKey,
                size,
                ReadMetadata(result.Metadata, Sha256MetadataKey),
                result.ETag,
                lastModified),
            size);
    }

    public async Task<ObjectStorageObjectInfo> CopyAsync(
        ObjectStorageCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var client = CreateClient(request.Connection);
        if (request.Size < SingleCopyMaximumSize)
        {
            await client.CopyObjectAsync(
                new CopyObjectRequest
                {
                    Bucket = request.Connection.Profile.BucketName,
                    Key = request.DestinationObjectKey,
                    SourceBucket = request.Connection.Profile.BucketName,
                    SourceKey = request.SourceObjectKey,
                    IfMatch = request.SourceETag,
                    MetadataDirective = "COPY",
                    ForbidOverwrite = true
                },
                cancellationToken: cancellationToken);
        }
        else
        {
            await CopyMultipartAsync(client, request, cancellationToken);
        }

        return await StatWithClientAsync(
            client,
            request.Connection.Profile.BucketName,
            request.DestinationObjectKey,
            cancellationToken)
            ?? throw new IOException("OSS 复制完成后未找到目标对象。");
    }

    public async Task DeleteAsync(
        ObjectStorageConnection connection,
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        using var client = CreateClient(connection);
        await client.DeleteObjectAsync(
            new DeleteObjectRequest
            {
                Bucket = connection.Profile.BucketName,
                Key = objectKey
            },
            cancellationToken: cancellationToken);
    }

    public async Task AbortMultipartUploadAsync(
        ObjectStorageConnection connection,
        MultipartUploadSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(session);
        using var client = CreateClient(connection);
        try
        {
            await client.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest
                {
                    Bucket = connection.Profile.BucketName,
                    Key = session.ObjectKey,
                    UploadId = session.UploadId
                },
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (IsMissingUpload(exception))
        {
            // The remote upload session has already expired or been removed.
        }
    }

    private static async Task CopyMultipartAsync(
        Client client,
        ObjectStorageCopyRequest request,
        CancellationToken cancellationToken)
    {
        var bucket = request.Connection.Profile.BucketName;
        var partSize = ComputePartSize(request.Size);
        var totalParts = checked((int)((request.Size + partSize - 1) / partSize));
        var initiated = await client.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                Bucket = bucket,
                Key = request.DestinationObjectKey,
                ForbidOverwrite = true,
                Metadata = CreateMetadata(request)
            },
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(initiated.UploadId))
        {
            throw new IOException("OSS 未返回云端复制任务 ID。");
        }

        try
        {
            var parts = new List<UploadPart>(totalParts);
            for (var partNumber = 1; partNumber <= totalParts; partNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = (partNumber - 1L) * partSize;
                var size = Math.Min(partSize, request.Size - offset);
                var result = await client.UploadPartCopyAsync(
                    new UploadPartCopyRequest
                    {
                        Bucket = bucket,
                        Key = request.DestinationObjectKey,
                        UploadId = initiated.UploadId,
                        PartNumber = partNumber,
                        SourceBucket = bucket,
                        SourceKey = request.SourceObjectKey,
                        SourceRange = $"bytes={offset}-{offset + size - 1}",
                        IfMatch = request.SourceETag
                    },
                    cancellationToken: cancellationToken);
                if (string.IsNullOrWhiteSpace(result.ETag))
                {
                    throw new IOException("OSS 云端复制分片响应缺少 ETag。");
                }

                parts.Add(new UploadPart
                {
                    PartNumber = partNumber,
                    ETag = result.ETag
                });
            }

            await client.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest
                {
                    Bucket = bucket,
                    Key = request.DestinationObjectKey,
                    UploadId = initiated.UploadId,
                    ForbidOverwrite = true,
                    CompleteMultipartUpload = new CompleteMultipartUpload
                    {
                        Parts = parts
                    }
                },
                cancellationToken: cancellationToken);
        }
        catch
        {
            try
            {
                await client.AbortMultipartUploadAsync(
                    new AbortMultipartUploadRequest
                    {
                        Bucket = bucket,
                        Key = request.DestinationObjectKey,
                        UploadId = initiated.UploadId
                    },
                    cancellationToken: CancellationToken.None);
            }
            catch
            {
            }

            throw;
        }
    }

    private static async Task UploadSingleObjectAsync(
        Client client,
        ObjectStorageTransferRequest request,
        IProgress<ObjectStorageTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = OpenSource(request.SourcePath);
        var result = await client.PutObjectAsync(
            new PutObjectRequest
            {
                Bucket = request.Connection.Profile.BucketName,
                Key = request.ObjectKey,
                Body = source,
                ContentLength = request.Size,
                ForbidOverwrite = true,
                Metadata = CreateMetadata(request),
                ProgressFn = (_, transferred, total) =>
                    progress?.Report(new ObjectStorageTransferProgress(
                        transferred,
                        transferred,
                        total,
                        CompletedParts: transferred >= total ? 1 : 0,
                        TotalParts: 1,
                        "正在上传"))
            },
            cancellationToken: cancellationToken);
        progress?.Report(new ObjectStorageTransferProgress(
            request.Size,
            request.Size,
            request.Size,
            CompletedParts: 1,
            TotalParts: 1,
            $"上传完成 · {result.ETag}"));
    }

    private static async Task UploadMultipartAsync(
        Client client,
        ObjectStorageTransferRequest request,
        Func<MultipartUploadSession, CancellationToken, Task> saveCheckpoint,
        IProgress<ObjectStorageTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partSize = ComputePartSize(request.Size);
        var totalParts = checked((int)((request.Size + partSize - 1) / partSize));
        var session = request.Session;
        if (session is not null)
        {
            ValidateSession(request, session);
            partSize = session.PartSize;
            totalParts = checked((int)((request.Size + partSize - 1) / partSize));
        }

        Dictionary<long, MultipartUploadPart> uploadedParts;
        if (session is null)
        {
            session = await InitiateMultipartUploadAsync(
                client,
                request,
                partSize,
                cancellationToken);
            uploadedParts = [];
            await saveCheckpoint(session, cancellationToken);
        }
        else
        {
            try
            {
                uploadedParts = await ListUploadedPartsAsync(
                    client,
                    request.Connection.Profile.BucketName,
                    request.ObjectKey,
                    session.UploadId,
                    cancellationToken);
            }
            catch (Exception exception) when (IsMissingUpload(exception))
            {
                session = await InitiateMultipartUploadAsync(
                    client,
                    request,
                    partSize,
                    cancellationToken);
                uploadedParts = [];
                await saveCheckpoint(session, cancellationToken);
            }
        }

        var completedBytes = uploadedParts.Values.Sum(part => part.Size);
        var currentRunTransferredBytes = 0L;
        progress?.Report(new ObjectStorageTransferProgress(
            completedBytes,
            currentRunTransferredBytes,
            request.Size,
            uploadedParts.Count,
            totalParts,
            uploadedParts.Count == 0 ? "开始分片上传" : "继续分片上传"));

        for (var partNumber = 1; partNumber <= totalParts; partNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = (partNumber - 1L) * partSize;
            var size = Math.Min(partSize, request.Size - offset);
            if (uploadedParts.TryGetValue(partNumber, out var existingPart) &&
                existingPart.Size == size)
            {
                continue;
            }

            var partResult = await UploadPartAsync(
                client,
                request,
                session.UploadId,
                partNumber,
                offset,
                size,
                completedBytes,
                currentRunTransferredBytes,
                uploadedParts.Count,
                totalParts,
                progress,
                cancellationToken);
            var etag = partResult.ETag;
            if (string.IsNullOrWhiteSpace(etag))
            {
                throw new IOException(
                    "OSS 分片上传响应缺少 ETag，已保留会话等待重试。");
            }

            uploadedParts[partNumber] = new MultipartUploadPart(
                partNumber,
                etag,
                size);
            completedBytes += size;
            currentRunTransferredBytes += size;
            session = session with
            {
                Parts = uploadedParts.Values
                    .OrderBy(part => part.PartNumber)
                    .ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await saveCheckpoint(session, cancellationToken);
            progress?.Report(new ObjectStorageTransferProgress(
                completedBytes,
                currentRunTransferredBytes,
                request.Size,
                uploadedParts.Count,
                totalParts,
                $"已上传分片 {partNumber:N0}/{totalParts:N0}"));
        }

        var completeParts = uploadedParts.Values
            .OrderBy(part => part.PartNumber)
            .Select(part => new UploadPart
            {
                PartNumber = part.PartNumber,
                ETag = part.ETag
            })
            .ToList();
        if (completeParts.Count != totalParts)
        {
            throw new IOException("OSS 分片数量不完整，保留会话等待重试。");
        }

        await client.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                Bucket = request.Connection.Profile.BucketName,
                Key = request.ObjectKey,
                UploadId = session.UploadId,
                ForbidOverwrite = true,
                CompleteMultipartUpload = new CompleteMultipartUpload
                {
                    Parts = completeParts
                }
            },
            cancellationToken: cancellationToken);
    }

    private static async Task<UploadPartResult> UploadPartAsync(
        Client client,
        ObjectStorageTransferRequest request,
        string uploadId,
        int partNumber,
        long offset,
        long size,
        long completedBytes,
        long currentRunTransferredBytes,
        int completedParts,
        int totalParts,
        IProgress<ObjectStorageTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var file = OpenSource(request.SourcePath);
        using var body = new BoundedStream(file, offset, size);
        return await client.UploadPartAsync(
            new UploadPartRequest
            {
                Bucket = request.Connection.Profile.BucketName,
                Key = request.ObjectKey,
                UploadId = uploadId,
                PartNumber = partNumber,
                ContentLength = size,
                Body = body,
                ProgressFn = (_, transferred, _) =>
                    progress?.Report(new ObjectStorageTransferProgress(
                        completedBytes + transferred,
                        currentRunTransferredBytes + transferred,
                        request.Size,
                        completedParts,
                        totalParts,
                        $"正在上传分片 {partNumber:N0}/{totalParts:N0}"))
            },
            cancellationToken: cancellationToken);
    }

    private static async Task<MultipartUploadSession> InitiateMultipartUploadAsync(
        Client client,
        ObjectStorageTransferRequest request,
        long partSize,
        CancellationToken cancellationToken)
    {
        var result = await client.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                Bucket = request.Connection.Profile.BucketName,
                Key = request.ObjectKey,
                ForbidOverwrite = true,
                Metadata = CreateMetadata(request)
            },
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(result.UploadId))
        {
            throw new IOException("OSS 未返回分片上传 ID。");
        }

        return new MultipartUploadSession(
            request.Connection.Profile.Id,
            request.AssetId,
            request.ObjectKey,
            Path.GetFullPath(request.SourcePath),
            result.UploadId,
            partSize,
            request.Size,
            request.ModifiedAt,
            [],
            DateTimeOffset.UtcNow);
    }

    private static async Task<Dictionary<long, MultipartUploadPart>>
        ListUploadedPartsAsync(
            Client client,
            string bucket,
            string objectKey,
            string uploadId,
            CancellationToken cancellationToken)
    {
        var parts = new Dictionary<long, MultipartUploadPart>();
        var paginator = client.ListPartsPaginator(new ListPartsRequest
        {
            Bucket = bucket,
            Key = objectKey,
            UploadId = uploadId
        });
        await foreach (var page in paginator.IterPageAsync(cancellationToken))
        {
            foreach (var part in page.Parts ?? [])
            {
                if (part.PartNumber is null ||
                    part.Size is null ||
                    string.IsNullOrWhiteSpace(part.ETag))
                {
                    continue;
                }

                parts[part.PartNumber.Value] = new MultipartUploadPart(
                    part.PartNumber.Value,
                    part.ETag,
                    part.Size.Value);
            }
        }

        return parts;
    }

    private static async Task<ObjectStorageObjectInfo?> StatWithClientAsync(
        Client client,
        string bucket,
        string objectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.HeadObjectAsync(
                new HeadObjectRequest
                {
                    Bucket = bucket,
                    Key = objectKey
                },
                cancellationToken: cancellationToken);
            if (result.ContentLength is null)
            {
                throw new IOException("OSS 对象元数据缺少 Content-Length。");
            }

            DateTimeOffset? lastModified = null;
            if (DateTimeOffset.TryParse(
                    result.LastModified,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                lastModified = parsed;
            }

            return new ObjectStorageObjectInfo(
                objectKey,
                result.ContentLength.Value,
                ReadMetadata(result.Metadata, Sha256MetadataKey),
                result.ETag,
                lastModified);
        }
        catch (Exception exception) when (IsMissingObject(exception))
        {
            return null;
        }
    }

    private static Client CreateClient(ObjectStorageConnection connection)
    {
        var profile = connection.Profile;
        var configuration = Configuration.LoadDefault();
        configuration.CredentialsProvider = string.IsNullOrWhiteSpace(
            connection.SecurityToken)
            ? new StaticCredentialsProvider(
                profile.AccessKeyId,
                connection.AccessKeySecret)
            : new StaticCredentialsProvider(
                profile.AccessKeyId,
                connection.AccessKeySecret,
                connection.SecurityToken);
        configuration.Region = ResolveRegion(profile);
        configuration.Endpoint =
            $"{(profile.UseHttps ? "https" : "http")}://{profile.Endpoint}";
        configuration.DisableSsl = !profile.UseHttps;
        configuration.RetryMaxAttempts = 3;
        return new Client(configuration);
    }

    private static string ResolveRegion(ObjectStorageProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Region))
        {
            return profile.Region;
        }

        var endpointLabel = profile.Endpoint.Split('.', 2)[0];
        if (endpointLabel.StartsWith("oss-", StringComparison.OrdinalIgnoreCase))
        {
            var region = endpointLabel[4..];
            if (region.EndsWith("-internal", StringComparison.OrdinalIgnoreCase))
            {
                region = region[..^"-internal".Length];
            }

            if (!string.IsNullOrWhiteSpace(region))
            {
                return region;
            }
        }

        throw new InvalidOperationException(
            "无法从 Endpoint 推断 OSS 地域，请在备份配置中填写地域。");
    }

    private static Dictionary<string, string> CreateMetadata(
        ObjectStorageTransferRequest request)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Sha256MetadataKey] = request.Sha256,
            [AssetIdMetadataKey] = request.AssetId.ToString("D")
        };
    }

    private static Dictionary<string, string> CreateMetadata(
        ObjectStorageCopyRequest request)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AssetIdMetadataKey] = request.AssetId.ToString("D")
        };
        if (!string.IsNullOrWhiteSpace(request.Sha256))
        {
            metadata[Sha256MetadataKey] = request.Sha256;
        }

        return metadata;
    }

    private static string? ReadMetadata(
        IDictionary<string, string>? metadata,
        string key)
    {
        if (metadata is null)
        {
            return null;
        }

        foreach (var pair in metadata)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static long ComputePartSize(long fileSize)
    {
        var required = (fileSize + MaximumPartCount - 1) / MaximumPartCount;
        if (required <= DefaultPartSize)
        {
            return DefaultPartSize;
        }

        const long mebibyte = 1024 * 1024;
        return ((required + mebibyte - 1) / mebibyte) * mebibyte;
    }

    private static void ValidateSession(
        ObjectStorageTransferRequest request,
        MultipartUploadSession session)
    {
        if (session.StorageProfileId != request.Connection.Profile.Id ||
            session.AssetId != request.AssetId ||
            !string.Equals(
                session.ObjectKey,
                request.ObjectKey,
                StringComparison.Ordinal) ||
            !PathsEqual(session.SourcePath, request.SourcePath) ||
            session.SourceSize != request.Size ||
            session.SourceModifiedAt != request.ModifiedAt ||
            session.PartSize <= 0)
        {
            throw new InvalidOperationException("本地分片会话与当前源文件不一致。");
        }
    }

    private static FileInfo ReadAndValidateSource(
        ObjectStorageTransferRequest request)
    {
        var info = new FileInfo(request.SourcePath);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileNotFoundException("待备份文件不存在。", request.SourcePath);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("不上传符号链接指向的文件。");
        }

        EnsureSourceMatchesRequest(request, info);
        return info;
    }

    private static void EnsureSourceUnchanged(
        ObjectStorageTransferRequest request,
        FileInfo before)
    {
        var after = new FileInfo(request.SourcePath);
        after.Refresh();
        if (!after.Exists ||
            after.Length != before.Length ||
            after.LastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            throw new IOException("上传期间源文件发生变化，已停止登记备份。");
        }

        EnsureSourceMatchesRequest(request, after);
    }

    private static void EnsureSourceMatchesRequest(
        ObjectStorageTransferRequest request,
        FileInfo info)
    {
        if (info.Length != request.Size ||
            info.LastWriteTimeUtc != request.ModifiedAt.UtcDateTime)
        {
            throw new IOException("源文件已变化，请重新扫描后再备份。");
        }
    }

    private static FileStream OpenSource(string path)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 1024 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
    }

    internal static bool IsMissingObject(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ContainsServiceException(
            exception,
            serviceException => serviceException.StatusCode == 404 ||
                string.Equals(
                    serviceException.ErrorCode,
                    "NoSuchKey",
                    StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsMissingUpload(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ContainsServiceException(
            exception,
            serviceException => serviceException.StatusCode == 404 ||
                string.Equals(
                    serviceException.ErrorCode,
                    "NoSuchUpload",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsServiceException(
        Exception exception,
        Func<ServiceException, bool> predicate)
    {
        if (exception is ServiceException serviceException)
        {
            return predicate(serviceException);
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException.Flatten().InnerExceptions.Any(
                innerException => ContainsServiceException(innerException, predicate));
        }

        return exception.InnerException is not null &&
            ContainsServiceException(exception.InnerException, predicate);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
