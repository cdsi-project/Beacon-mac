using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Core.Abstractions;

public interface IFileScanner
{
    Task ScanAsync(
        string rootPath,
        IReadOnlyCollection<string> excludedDirectoryPaths,
        Func<DiscoveredFile, CancellationToken, ValueTask> onFile,
        Func<ScanError, CancellationToken, ValueTask> onError,
        CancellationToken cancellationToken);
}
