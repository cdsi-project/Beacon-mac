using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Fingerprints;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Infrastructure.Fingerprints;

public sealed class Sha256FileFingerprintService : IFileFingerprintService
{
    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    public async Task<FileFingerprint> CalculateAsync(
        DiscoveredFile file,
        Action<FileHashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();

        var before = ReadCurrentMetadata(file);
        EnsureUnchanged(file, before);

        await using var stream = new FileStream(
            file.FullPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = BufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var bytesProcessed = 0L;
        var lastProgressTimestamp = Stopwatch.GetTimestamp();

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
                bytesProcessed += bytesRead;

                if (progress is not null &&
                    Stopwatch.GetElapsedTime(lastProgressTimestamp) >= ProgressInterval)
                {
                    progress(new FileHashProgress(bytesProcessed, before.Length));
                    lastProgressTimestamp = Stopwatch.GetTimestamp();
                }
            }

            progress?.Invoke(new FileHashProgress(bytesProcessed, before.Length));
            var hashBytes = hash.GetHashAndReset();

            var after = ReadCurrentMetadata(file);
            EnsureUnchanged(file, after);

            return new FileFingerprint(
                Convert.ToHexStringLower(hashBytes),
                after.Length,
                new DateTimeOffset(after.LastWriteTimeUtc));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileInfo ReadCurrentMetadata(DiscoveredFile file)
    {
        var info = new FileInfo(file.FullPath);
        info.Refresh();

        if (!info.Exists)
        {
            throw new FileNotFoundException("File disappeared before fingerprinting.", file.FullPath);
        }

        return info;
    }

    private static void EnsureUnchanged(DiscoveredFile expected, FileInfo actual)
    {
        if (actual.Length != expected.Size ||
            actual.LastWriteTimeUtc != expected.ModifiedAt.UtcDateTime)
        {
            throw new FileChangedDuringFingerprintException(expected);
        }
    }
}
