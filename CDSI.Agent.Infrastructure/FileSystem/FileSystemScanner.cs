using System.Security;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Infrastructure.FileSystem;

public sealed class FileSystemScanner : IFileScanner
{
    private readonly FileSystemScannerOptions _options;

    public FileSystemScanner(FileSystemScannerOptions? options = null)
    {
        _options = options ?? new FileSystemScannerOptions();
    }

    public async Task ScanAsync(
        string rootPath,
        IReadOnlyCollection<string> excludedDirectoryPaths,
        Func<DiscoveredFile, CancellationToken, ValueTask> onFile,
        Func<ScanError, CancellationToken, ValueTask> onError,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(excludedDirectoryPaths);
        ArgumentNullException.ThrowIfNull(onFile);
        ArgumentNullException.ThrowIfNull(onError);

        var normalizedRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException($"Scan root does not exist: {normalizedRoot}");
        }

        var excludedDirectories = excludedDirectoryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();

        var pendingDirectories = new Stack<string>();
        var visitedDirectories = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        pendingDirectories.Push(normalizedRoot);

        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedDirectory = Path.GetFullPath(currentDirectory);
            if (!visitedDirectories.Add(normalizedDirectory) ||
                excludedDirectories.Any(excluded =>
                    IsSameOrDescendant(normalizedDirectory, excluded)))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(normalizedDirectory);
            }
            catch (Exception exception) when (IsRecoverableFileSystemError(exception))
            {
                await onError(
                    new ScanError(normalizedDirectory, exception.Message),
                    cancellationToken);
                files = [];
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var file = new FileInfo(filePath);
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var extension = file.Extension.ToLowerInvariant();
                    await onFile(
                        new DiscoveredFile(
                            file.FullName,
                            file.Name,
                            extension,
                            MimeTypeDetector.Detect(extension),
                            file.Length,
                            new DateTimeOffset(file.CreationTimeUtc),
                            new DateTimeOffset(file.LastWriteTimeUtc)),
                        cancellationToken);
                }
                catch (Exception exception) when (IsRecoverableFileSystemError(exception))
                {
                    await onError(new ScanError(filePath, exception.Message), cancellationToken);
                }
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(normalizedDirectory);
            }
            catch (Exception exception) when (IsRecoverableFileSystemError(exception))
            {
                await onError(
                    new ScanError(normalizedDirectory, exception.Message),
                    cancellationToken);
                continue;
            }

            foreach (var directoryPath in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var directory = new DirectoryInfo(directoryPath);
                    if (_options.IgnoredDirectoryNames.Contains(directory.Name) ||
                        (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pendingDirectories.Push(directory.FullName);
                }
                catch (Exception exception) when (IsRecoverableFileSystemError(exception))
                {
                    await onError(new ScanError(directoryPath, exception.Message), cancellationToken);
                }
            }
        }
    }

    private static bool IsRecoverableFileSystemError(Exception exception)
    {
        return exception is UnauthorizedAccessException
            or IOException
            or SecurityException
            or NotSupportedException;
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(candidate, parent, comparison))
        {
            return true;
        }

        var relative = Path.GetRelativePath(parent, candidate);
        return !Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }
}
