namespace CDSI.Agent.Infrastructure.FileSystem;

public sealed class FileSystemScannerOptions
{
    public ISet<string> IgnoredDirectoryNames { get; } = new HashSet<string>(
        [
            ".git",
            ".hg",
            ".svn",
            ".vs",
            "bin",
            "node_modules",
            "obj",
            "vendor"
        ],
        StringComparer.OrdinalIgnoreCase);
}
