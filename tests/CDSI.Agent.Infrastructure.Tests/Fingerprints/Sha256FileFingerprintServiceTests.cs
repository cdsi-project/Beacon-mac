using CDSI.Agent.Core.Fingerprints;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Infrastructure.Fingerprints;

namespace CDSI.Agent.Infrastructure.Tests.Fingerprints;

public sealed class Sha256FileFingerprintServiceTests
{
    [Fact]
    public async Task CalculateAsync_ReturnsTheExpectedSha256()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "hello.txt");
        await File.WriteAllTextAsync(path, "hello");
        var discoveredFile = CreateDiscoveredFile(path);
        var service = new Sha256FileFingerprintService();

        var reports = new List<FileHashProgress>();
        var fingerprint = await service.CalculateAsync(discoveredFile, reports.Add);

        Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            fingerprint.Sha256);
        Assert.Equal(discoveredFile.Size, fingerprint.Size);
        Assert.NotEmpty(reports);
        Assert.Equal(discoveredFile.Size, reports[^1].BytesProcessed);
        Assert.Equal(discoveredFile.ModifiedAt, fingerprint.ModifiedAt);
    }

    [Fact]
    public async Task CalculateAsync_WhenMetadataChanged_RejectsTheStaleSnapshot()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "changing.txt");
        await File.WriteAllTextAsync(path, "before");
        var discoveredFile = CreateDiscoveredFile(path);
        await File.AppendAllTextAsync(path, "-after");
        var service = new Sha256FileFingerprintService();

        await Assert.ThrowsAsync<FileChangedDuringFingerprintException>(
            () => service.CalculateAsync(discoveredFile));
    }

    private static DiscoveredFile CreateDiscoveredFile(string path)
    {
        var info = new FileInfo(path);
        return new DiscoveredFile(
            info.FullName,
            info.Name,
            info.Extension.ToLowerInvariant(),
            "text/plain",
            info.Length,
            new DateTimeOffset(info.CreationTimeUtc),
            new DateTimeOffset(info.LastWriteTimeUtc));
    }
}
