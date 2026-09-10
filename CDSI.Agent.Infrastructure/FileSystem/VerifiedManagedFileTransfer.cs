using System.Buffers;
using System.Security.Cryptography;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Transfers;

namespace CDSI.Agent.Infrastructure.FileSystem;

public sealed class VerifiedManagedFileTransfer : IManagedFileTransfer
{
    private const int BufferSize = 1024 * 1024;

    public async Task<VerifiedManagedFileCopy> CopyAndVerifyAsync(
        LocalAssetTransferSource source,
        string targetPath,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSource = Path.GetFullPath(source.Path);
        var normalizedTarget = Path.GetFullPath(targetPath);
        if (PathsEqual(normalizedSource, normalizedTarget))
        {
            var sameFileHash = await CalculateSha256Async(
                normalizedSource,
                progress,
                cancellationToken);
            EnsureExpectedHash(source, sameFileHash);
            return new VerifiedManagedFileCopy(source.Size, sameFileHash, true);
        }

        var before = ReadAndValidateSource(source, normalizedSource);
        var targetDirectory = Path.GetDirectoryName(normalizedTarget)
            ?? throw new InvalidOperationException("目标文件没有父目录。");
        EnsureExistingAncestorsAreNotReparsePoints(targetDirectory);
        Directory.CreateDirectory(targetDirectory);
        EnsureExistingAncestorsAreNotReparsePoints(targetDirectory);

        if (File.Exists(normalizedTarget))
        {
            var existing = await VerifyExistingTargetAsync(
                source,
                normalizedSource,
                normalizedTarget,
                progress,
                cancellationToken);
            EnsureSourceUnchanged(source, normalizedSource, before);
            return existing;
        }

        var temporaryPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(normalizedTarget)}.{Guid.NewGuid():N}.cdsi-part");
        try
        {
            var sourceHash = await CopyToTemporaryFileAsync(
                normalizedSource,
                temporaryPath,
                progress,
                cancellationToken);
            EnsureSourceUnchanged(source, normalizedSource, before);
            EnsureExpectedHash(source, sourceHash);

            var targetHash = await CalculateSha256Async(
                temporaryPath,
                progress: null,
                cancellationToken);
            if (!string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("工作目录副本校验失败，未替换任何现有文件。");
            }

            var copiedInfo = new FileInfo(temporaryPath);
            copiedInfo.Refresh();
            if (copiedInfo.Length != source.Size)
            {
                throw new IOException("工作目录副本大小与源文件不一致。");
            }

            File.Move(temporaryPath, normalizedTarget, overwrite: false);
            File.SetLastWriteTimeUtc(normalizedTarget, before.LastWriteTimeUtc);
            return new VerifiedManagedFileCopy(source.Size, targetHash, false);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public Task DeleteSourceAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = Path.GetFullPath(sourcePath);
        File.Delete(normalizedPath);
        if (File.Exists(normalizedPath))
        {
            throw new IOException("源文件仍然存在，移动操作没有完成。");
        }

        return Task.CompletedTask;
    }

    private static async Task<VerifiedManagedFileCopy> VerifyExistingTargetAsync(
        LocalAssetTransferSource source,
        string sourcePath,
        string targetPath,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var targetInfo = new FileInfo(targetPath);
        targetInfo.Refresh();
        if (targetInfo.Length != source.Size)
        {
            throw new IOException("目标文件已存在且内容不同，未覆盖现有文件。");
        }

        var sourceHash = await CalculateSha256Async(
            sourcePath,
            progress,
            cancellationToken);
        EnsureExpectedHash(source, sourceHash);
        var targetHash = await CalculateSha256Async(
            targetPath,
            progress: null,
            cancellationToken);
        if (!string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("目标文件已存在且内容不同，未覆盖现有文件。");
        }

        return new VerifiedManagedFileCopy(source.Size, targetHash, true);
    }

    private static async Task<string> CopyToTemporaryFileAsync(
        string sourcePath,
        string temporaryPath,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        await using var input = OpenSequentialRead(sourcePath);
        await using var output = new FileStream(
            temporaryPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var processed = 0L;
        try
        {
            while (true)
            {
                var bytesRead = await input.ReadAsync(
                    buffer.AsMemory(0, BufferSize),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                await output.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
                hash.AppendData(buffer, 0, bytesRead);
                processed += bytesRead;
                progress?.Invoke(processed);
            }

            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            progress?.Invoke(processed);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> CalculateSha256Async(
        string path,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = OpenSequentialRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var processed = 0L;
        try
        {
            while (true)
            {
                var bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(0, BufferSize),
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, bytesRead);
                processed += bytesRead;
                progress?.Invoke(processed);
            }

            progress?.Invoke(processed);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileStream OpenSequentialRead(string path)
    {
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
    }

    private static FileInfo ReadAndValidateSource(
        LocalAssetTransferSource source,
        string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileNotFoundException("源文件不存在。", sourcePath);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("不复制符号链接或 junction 指向的文件。");
        }

        EnsureSourceMatchesIndex(source, info);
        return info;
    }

    private static void EnsureSourceUnchanged(
        LocalAssetTransferSource source,
        string sourcePath,
        FileInfo before)
    {
        var after = new FileInfo(sourcePath);
        after.Refresh();
        if (!after.Exists ||
            after.Length != before.Length ||
            after.LastWriteTimeUtc != before.LastWriteTimeUtc)
        {
            throw new IOException("复制期间源文件发生变化，已取消本次操作。");
        }

        EnsureSourceMatchesIndex(source, after);
    }

    private static void EnsureSourceMatchesIndex(
        LocalAssetTransferSource source,
        FileInfo info)
    {
        if (info.Length != source.Size ||
            info.LastWriteTimeUtc != source.ModifiedAt.UtcDateTime)
        {
            throw new IOException("源文件已变化，请重新扫描后再操作。");
        }
    }

    private static void EnsureExpectedHash(
        LocalAssetTransferSource source,
        string actualHash)
    {
        if (source.Sha256 is not null &&
            !string.Equals(
                source.Sha256,
                actualHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("源文件哈希与索引不一致，请重新扫描后再操作。");
        }
    }

    private static void EnsureExistingAncestorsAreNotReparsePoints(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"目标目录不能位于符号链接或 junction 中: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The operation result remains authoritative; a locked temp file is harmless.
        }
    }
}
