namespace CDSI.Agent.Infrastructure.Tests;

internal sealed class TestDirectory : IDisposable
{
    private static readonly string TestRoot = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "cdsi-agent-tests");

    public TestDirectory()
    {
        Path = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        var resolvedPath = System.IO.Path.GetFullPath(Path);
        var resolvedRoot = System.IO.Path.GetFullPath(TestRoot);

        if (!resolvedPath.StartsWith(
                resolvedRoot + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a non-test directory.");
        }

        if (Directory.Exists(resolvedPath))
        {
            Directory.Delete(resolvedPath, recursive: true);
        }
    }
}
