using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Infrastructure.Storage;

public sealed class S3CompatibleStorageAdapter : IObjectStorageAdapter
{
    private const long DefaultPartSize = 16L * 1024 * 1024;
    private const long SingleCopyMaximumSize = 1024L * 1024 * 1024;
    private const long MaximumPartCount = 10_000;
    private const string Sha256MetadataKey = "cdsi-sha256";
    private const string AssetIdMetadataKey = "cdsi-asset-id";

    private readonly bool _forcePathStyle;

    public S3CompatibleStorageAdapter(ObjectStorageProvider provider)
    {
        if (provider is not (ObjectStorageProvider.QiniuKodo or
            ObjectStorageProvider.TencentCos))
        {
            throw new ArgumentOutOfRangeException(
                nameof(provider),
                "S3 兼容适配器仅支持七牛云 Kodo 和腾讯云 COS。");
        }

        Provider = provider;
        _forcePathStyle = provider == ObjectStorageProvider.QiniuKodo;
    }

    public ObjectStorageProvider Provider { get; }

    internal bool ForcePathStyle => _forcePathStyle;

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
            await UploadSingleObjectAsync(client, request, progress, cancellationToken);
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
            ?? throw new IOException("S3 兼容存储上传完成后未找到目标对象。");
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
        using var response = await client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = connection.Profile.BucketName,
                Key = objectKey
            },
            cancellationToken);
        var total = response.ContentLength;
        var downloaded = 0L;
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await response.ResponseStream.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            progress?.Report(new ObjectStorageDownloadProgress(
                downloaded,
                total,
                "正在下载"));
        }

        progress?.Report(new ObjectStorageDownloadProgress(
            downloaded,
            total,
            "下载完成"));
        return new ObjectStorageDownloadResult(
            new ObjectStorageObjectInfo(
                objectKey,
                total,
                ReadMetadata(response.Metadata, Sha256MetadataKey),
                response.ETag,
                response.LastModified is null
                    ? null
                    : new DateTimeOffset(response.LastModified.Value, TimeSpan.Zero)),
            downloaded);
    }

    public async Task<ObjectStorageObjectInfo> CopyAsync(
        ObjectStorageCopyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var client = CreateClient(request.Connection);
        if (request.Size < SingleCopyMaximumSize)
        {
            var copy = new CopyObjectRequest
            {
                DestinationBucket = request.Connection.Profile.BucketName,
                DestinationKey = request.DestinationObjectKey,
                SourceBucket = request.Connection.Profile.BucketName,
                SourceKey = request.SourceObjectKey,
                ETagToMatch = request.SourceETag,
                MetadataDirective = S3MetadataDirective.COPY
            };
            await client.CopyObjectAsync(copy, cancellationToken);
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
            ?? throw new IOException("S3 兼容存储复制完成后未找到目标对象。");
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
                BucketName = connection.Profile.BucketName,
                Key = objectKey
            },
            cancellationToken);
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
                    BucketName = connection.Profile.BucketName,
                    Key = session.ObjectKey,
                    UploadId = session.UploadId
                },
                cancellationToken);
        }
        catch (Exception exception) when (IsMissingUpload(exception))
        {
        }
    }

    private static async Task CopyMultipartAsync(
        IAmazonS3 client,
        ObjectStorageCopyRequest request,
        CancellationToken cancellationToken)
    {
        var bucket = request.Connection.Profile.BucketName;
        var upload = new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = request.DestinationObjectKey
        };
        AddMetadata(upload.Metadata, request);
        var initiated = await client.InitiateMultipartUploadAsync(
            upload,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(initiated.UploadId))
        {
            throw new IOException("S3 兼容存储未返回云端复制任务 ID。");
        }

        try
        {
            var partSize = ComputePartSize(request.Size);
            var totalParts = checked((int)((request.Size + partSize - 1) / partSize));
            var parts = new List<PartETag>(totalParts);
            for (var partNumber = 1; partNumber <= totalParts; partNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var firstByte = (partNumber - 1L) * partSize;
                var lastByte = Math.Min(firstByte + partSize, request.Size) - 1;
                var result = await client.CopyPartAsync(
                    new CopyPartRequest
                    {
                        DestinationBucket = bucket,
                        DestinationKey = request.DestinationObjectKey,
                        UploadId = initiated.UploadId,
                        PartNumber = partNumber,
                        SourceBucket = bucket,
                        SourceKey = request.SourceObjectKey,
                        FirstByte = firstByte,
                        LastByte = lastByte,
                        ETagToMatch = string.IsNullOrWhiteSpace(request.SourceETag)
                            ? []
                            : [request.SourceETag]
                    },
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(result.ETag))
                {
                    throw new IOException("S3 兼容存储云端复制分片响应缺少 ETag。");
                }

                parts.Add(new PartETag(partNumber, result.ETag));
            }

            await client.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = request.DestinationObjectKey,
                    UploadId = initiated.UploadId,
                    PartETags = parts,
                    IfNoneMatch = GetIfNoneMatch(request.Connection.Profile.Provider)
                },
                cancellationToken);
        }
        catch
        {
            try
            {
                await client.AbortMultipartUploadAsync(
                    new AbortMultipartUploadRequest
                    {
                        BucketName = bucket,
                        Key = request.DestinationObjectKey,
                        UploadId = initiated.UploadId
                    },
                    CancellationToken.None);
            }
            catch
            {
            }

            throw;
        }
    }

    private static async Task UploadSingleObjectAsync(
        IAmazonS3 client,
        ObjectStorageTransferRequest request,
        IProgress<ObjectStorageTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = OpenSource(request.SourcePath);
        using var progressStream = new ProgressReadStream(
            source,
            transferred => progress?.Report(new ObjectStorageTransferProgress(
                transferred,
                transferred,
                request.Size,
                transferred >= request.Size ? 1 : 0,
                TotalParts: 1,
                "正在上传")));
        var upload = new PutObjectRequest
        {
            BucketName = request.Connection.Profile.BucketName,
            Key = request.ObjectKey,
            InputStream = progressStream,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            IfNoneMatch = GetIfNoneMatch(request.Connection.Profile.Provider)
        };
        AddMetadata(upload.Metadata, request);
        var result = await client.PutObjectAsync(upload, cancellationToken);
        progress?.Report(new ObjectStorageTransferProgress(
            request.Size,
            request.Size,
            request.Size,
            CompletedParts: 1,
            TotalParts: 1,
            $"上传完成 · {result.ETag}"));
    }

    private static async Task UploadMultipartAsync(
        IAmazonS3 client,
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

            var etag = await UploadPartAsync(
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

        var partEtags = uploadedParts.Values
            .OrderBy(part => part.PartNumber)
            .Select(part => new PartETag(checked((int)part.PartNumber), part.ETag))
            .ToList();
        if (partEtags.Count != totalParts)
        {
            throw new IOException("S3 兼容存储分片数量不完整，保留会话等待重试。");
        }

        await client.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = request.Connection.Profile.BucketName,
                Key = request.ObjectKey,
                UploadId = session.UploadId,
                PartETags = partEtags,
                IfNoneMatch = GetIfNoneMatch(request.Connection.Profile.Provider)
            },
            cancellationToken);
    }

    private static async Task<string> UploadPartAsync(
        IAmazonS3 client,
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
        await using var source = OpenSource(request.SourcePath);
        source.Position = offset;
        using var progressStream = new ProgressReadStream(
            source,
            transferred => progress?.Report(new ObjectStorageTransferProgress(
                completedBytes + transferred,
                currentRunTransferredBytes + transferred,
                request.Size,
                completedParts,
                totalParts,
                $"正在上传分片 {partNumber:N0}/{totalParts:N0}")));
        var result = await client.UploadPartAsync(
            new UploadPartRequest
            {
                BucketName = request.Connection.Profile.BucketName,
                Key = request.ObjectKey,
                UploadId = uploadId,
                PartNumber = partNumber,
                PartSize = size,
                FilePosition = offset,
                InputStream = progressStream
            },
            cancellationToken);
        return string.IsNullOrWhiteSpace(result.ETag)
            ? throw new IOException(
                "S3 兼容存储分片上传响应缺少 ETag，已保留会话等待重试。")
            : result.ETag;
    }

    private static async Task<MultipartUploadSession> InitiateMultipartUploadAsync(
        IAmazonS3 client,
        ObjectStorageTransferRequest request,
        long partSize,
        CancellationToken cancellationToken)
    {
        var upload = new InitiateMultipartUploadRequest
        {
            BucketName = request.Connection.Profile.BucketName,
            Key = request.ObjectKey
        };
        AddMetadata(upload.Metadata, request);
        var result = await client.InitiateMultipartUploadAsync(upload, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.UploadId))
        {
            throw new IOException("S3 兼容存储未返回分片上传 ID。");
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
            IAmazonS3 client,
            string bucket,
            string objectKey,
            string uploadId,
            CancellationToken cancellationToken)
    {
        var parts = new Dictionary<long, MultipartUploadPart>();
        string? marker = null;
        do
        {
            var response = await client.ListPartsAsync(
                new ListPartsRequest
                {
                    BucketName = bucket,
                    Key = objectKey,
                    UploadId = uploadId,
                    PartNumberMarker = marker
                },
                cancellationToken);
            foreach (var part in response.Parts ?? [])
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

            marker = response.IsTruncated == true
                ? response.NextPartNumberMarker?.ToString(CultureInfo.InvariantCulture)
                : null;
        }
        while (marker is not null);

        return parts;
    }

    private static async Task<ObjectStorageObjectInfo?> StatWithClientAsync(
        IAmazonS3 client,
        string bucket,
        string objectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = objectKey
                },
                cancellationToken);
            return new ObjectStorageObjectInfo(
                objectKey,
                result.ContentLength,
                ReadMetadata(result.Metadata, Sha256MetadataKey),
                result.ETag,
                result.LastModified is null
                    ? null
                    : new DateTimeOffset(result.LastModified.Value, TimeSpan.Zero));
        }
        catch (Exception exception) when (IsMissingObject(exception))
        {
            return null;
        }
    }

    private IAmazonS3 CreateClient(ObjectStorageConnection connection)
    {
        var profile = connection.Profile;
        if (string.IsNullOrWhiteSpace(profile.Region))
        {
            throw new InvalidOperationException(
                "S3 兼容备份配置缺少 Region ID，请在备份配置中补充。");
        }

        AWSCredentials credentials = string.IsNullOrWhiteSpace(connection.SecurityToken)
            ? new BasicAWSCredentials(profile.AccessKeyId, connection.AccessKeySecret)
            : new SessionAWSCredentials(
                profile.AccessKeyId,
                connection.AccessKeySecret,
                connection.SecurityToken);
        var client = new AmazonS3Client(credentials, new AmazonS3Config
        {
            ServiceURL = CreateServiceUrl(profile),
            AuthenticationRegion = profile.Region,
            ForcePathStyle = _forcePathStyle,
            UseHttp = !profile.UseHttps,
            MaxErrorRetry = 3
        });
        if (profile.Provider == ObjectStorageProvider.TencentCos)
        {
            client.BeforeRequestEvent += AddTencentOverwriteProtection;
        }

        return client;
    }

    private static string? GetIfNoneMatch(ObjectStorageProvider provider)
    {
        return provider == ObjectStorageProvider.TencentCos ? null : "*";
    }

    private static void AddTencentOverwriteProtection(
        object sender,
        RequestEventArgs eventArgs)
    {
        if (eventArgs is WebServiceRequestEventArgs requestEvent &&
            requestEvent.Request is PutObjectRequest or
                InitiateMultipartUploadRequest or
                CompleteMultipartUploadRequest or
                CopyObjectRequest)
        {
            requestEvent.Headers["x-cos-forbid-overwrite"] = "true";
        }
    }

    internal static string CreateServiceUrl(ObjectStorageProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return $"{(profile.UseHttps ? "https" : "http")}://{profile.Endpoint}";
    }

    private static void AddMetadata(
        MetadataCollection metadata,
        ObjectStorageTransferRequest request)
    {
        metadata[Sha256MetadataKey] = request.Sha256;
        metadata[AssetIdMetadataKey] = request.AssetId.ToString("D");
    }

    private static void AddMetadata(
        MetadataCollection metadata,
        ObjectStorageCopyRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Sha256))
        {
            metadata[Sha256MetadataKey] = request.Sha256;
        }

        metadata[AssetIdMetadataKey] = request.AssetId.ToString("D");
    }

    private static string? ReadMetadata(MetadataCollection metadata, string key)
    {
        foreach (var candidate in metadata.Keys)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    candidate,
                    $"x-amz-meta-{key}",
                    StringComparison.OrdinalIgnoreCase))
            {
                return metadata[candidate];
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
            !string.Equals(session.ObjectKey, request.ObjectKey, StringComparison.Ordinal) ||
            !PathsEqual(session.SourcePath, request.SourcePath) ||
            session.SourceSize != request.Size ||
            session.SourceModifiedAt != request.ModifiedAt ||
            session.PartSize <= 0)
        {
            throw new InvalidOperationException("本地分片会话与当前源文件不一致。");
        }
    }

    private static FileInfo ReadAndValidateSource(ObjectStorageTransferRequest request)
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
        return ContainsS3Exception(
            exception,
            serviceException => serviceException.StatusCode == HttpStatusCode.NotFound ||
                string.Equals(
                    serviceException.ErrorCode,
                    "NoSuchKey",
                    StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsMissingUpload(Exception exception)
    {
        return ContainsS3Exception(
            exception,
            serviceException => serviceException.StatusCode == HttpStatusCode.NotFound ||
                string.Equals(
                    serviceException.ErrorCode,
                    "NoSuchUpload",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsS3Exception(
        Exception exception,
        Func<AmazonS3Exception, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AmazonS3Exception serviceException)
        {
            return predicate(serviceException);
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException.Flatten().InnerExceptions.Any(
                innerException => ContainsS3Exception(innerException, predicate));
        }

        return exception.InnerException is not null &&
            ContainsS3Exception(exception.InnerException, predicate);
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

    private sealed class ProgressReadStream(
        Stream inner,
        Action<long> reportProgress) : Stream
    {
        private long _transferred;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Report(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Report(read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
        }

        private void Report(int count)
        {
            if (count <= 0)
            {
                return;
            }

            _transferred += count;
            reportProgress(_transferred);
        }
    }
}
