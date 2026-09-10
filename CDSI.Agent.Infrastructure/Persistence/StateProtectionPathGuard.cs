namespace CDSI.Agent.Infrastructure.Persistence;

internal static class StateProtectionPathGuard
{
    internal static string EnsureDirectory(string controlledRoot, string directory)
    {
        var root = Normalize(controlledRoot);
        var target = Normalize(directory);
        EnsureContained(root, target, allowRoot: true);

        Directory.CreateDirectory(root);
        EnsurePlainDirectory(root);
        if (PathsEqual(root, target))
        {
            return target;
        }

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, target)
                     .Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (TryGetAttributes(current, out var attributes) &&
                (attributes & FileAttributes.Directory) == 0)
            {
                throw new StateBackupValidationException(
                    $"状态保护路径被文件占用：{current}");
            }

            Directory.CreateDirectory(current);
            EnsurePlainDirectory(current);
        }

        return target;
    }

    internal static string ValidateExistingDirectory(
        string controlledRoot,
        string directory)
    {
        var root = Normalize(controlledRoot);
        var target = Normalize(directory);
        EnsureContained(root, target, allowRoot: true);
        EnsurePlainDirectory(root);

        if (PathsEqual(root, target))
        {
            return target;
        }

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, target)
                     .Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsurePlainDirectory(current);
        }

        return target;
    }

    internal static bool TryDeleteDirectory(string controlledRoot, string directory)
    {
        try
        {
            var root = Normalize(controlledRoot);
            var target = Normalize(directory);
            EnsureContained(root, target, allowRoot: false);
            if (!TryGetAttributes(target, out _))
            {
                return true;
            }

            ValidateExistingDirectory(root, target);
            EnsureTreeContainsNoReparsePoints(target);
            Directory.Delete(target, recursive: true);
            return !TryGetAttributes(target, out _);
        }
        catch (StateBackupValidationException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string ResetDirectory(string controlledRoot, string directory)
    {
        var target = Normalize(directory);
        if (TryGetAttributes(target, out _) &&
            !TryDeleteDirectory(controlledRoot, target))
        {
            throw new IOException($"无法安全清理状态保护目录：{target}");
        }

        return EnsureDirectory(controlledRoot, target);
    }

    internal static void EnsureContained(
        string controlledRoot,
        string path,
        bool allowRoot)
    {
        var root = Normalize(controlledRoot);
        var target = Normalize(path);
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
            (!allowRoot && PathsEqual(root, target)))
        {
            throw new StateBackupValidationException(
                "状态保护路径超出应用受控目录。");
        }
    }

    internal static bool PathsEqual(string left, string right) =>
        string.Equals(
            Normalize(left),
            Normalize(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    internal static bool TryGetAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void EnsureTreeContainsNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsurePlainDirectory(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StateBackupValidationException(
                        "状态保护目录包含符号链接或 junction，已拒绝递归清理。");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void EnsurePlainDirectory(string path)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            throw new DirectoryNotFoundException($"状态保护目录不存在：{path}");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new StateBackupValidationException(
                $"状态保护路径不是目录：{path}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new StateBackupValidationException(
                "状态保护目录不能包含符号链接或 junction。");
        }
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
