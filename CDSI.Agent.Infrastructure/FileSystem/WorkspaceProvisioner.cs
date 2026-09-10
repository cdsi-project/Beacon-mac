using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Workspaces;

namespace CDSI.Agent.Infrastructure.FileSystem;

public sealed class WorkspaceProvisioner : IWorkspaceProvisioner
{
    private static readonly string[] DirectoryNames =
        ["Inbox", "Assets", "Exports", "Cache", "Temp", "System"];

    public string NormalizeAndValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var normalizedPath = NormalizePath(path);
        var pathRoot = Path.GetPathRoot(normalizedPath);
        if (string.Equals(normalizedPath, pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("工作目录不能是磁盘或共享根目录。");
        }

        if (File.Exists(normalizedPath))
        {
            throw new InvalidOperationException("工作目录路径指向一个文件。");
        }

        EnsureExistingAncestorsAreNotReparsePoints(normalizedPath);
        return normalizedPath;
    }

    public Task<WorkspaceLayout> ProvisionAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizeAndValidatePath(path);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(normalizedPath);
        EnsureNotReparsePoint(normalizedPath);

        var directories = DirectoryNames.ToDictionary(
            name => name,
            name =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.Combine(normalizedPath, name);
                Directory.CreateDirectory(directory);
                EnsureNotReparsePoint(directory);
                return directory;
            },
            StringComparer.Ordinal);

        return Task.FromResult(new WorkspaceLayout(
            normalizedPath,
            directories["Inbox"],
            directories["Assets"],
            directories["Exports"],
            directories["Cache"],
            directories["Temp"],
            directories["System"]));
    }

    private static void EnsureExistingAncestorsAreNotReparsePoints(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists)
            {
                EnsureNotReparsePoint(current.FullName);
            }

            current = current.Parent;
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"工作目录不能位于符号链接或 junction 中: {path}");
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
