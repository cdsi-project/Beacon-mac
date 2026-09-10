using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Core.Abstractions;

public interface IObjectStorageAdapter
{
    ObjectStorageProvider Provider { get; }

    Task<ObjectStorageObjectInfo?> StatAsync(
        ObjectStorageConnection connection,
        string objectKey,
        CancellationToken cancellationToken = default);

    Task<ObjectStorageTransferResult> UploadAsync(
        ObjectStorageTransferRequest request,
        Func<MultipartUploadSession, CancellationToken, Task> saveCheckpoint,
        IProgress<ObjectStorageTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ObjectStorageDownloadResult> DownloadAsync(
        ObjectStorageConnection connection,
        string objectKey,
        Stream destination,
        IProgress<ObjectStorageDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ObjectStorageObjectInfo> CopyAsync(
        ObjectStorageCopyRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"{Provider} 存储适配器不支持云端对象重命名。");

    Task DeleteAsync(
        ObjectStorageConnection connection,
        string objectKey,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"{Provider} 存储适配器不支持删除云端对象。");

    Task AbortMultipartUploadAsync(
        ObjectStorageConnection connection,
        MultipartUploadSession session,
        CancellationToken cancellationToken = default);
}
