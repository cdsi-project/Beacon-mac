using CDSI.Agent.Application.Fingerprints;
using CDSI.Agent.Application.Metadata;
using CDSI.Agent.Application.Scanning;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Fingerprints;
using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Infrastructure.FileSystem;
using CDSI.Agent.Infrastructure.Fingerprints;
using CDSI.Agent.Infrastructure.Metadata;
using CDSI.Agent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace CDSI.Agent.IntegrationTests.Scanning;

public sealed class ScanApplicationServiceTests
{
    [Fact]
    public async Task ScanDirectoryAsync_IndexesIdempotentlyWithoutChangingSourceFiles()
    {
        using var directory = new TestDirectory();
        var scanRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "Assets"));
        var nested = Directory.CreateDirectory(Path.Combine(scanRoot.FullName, "Nested"));
        var ignored = Directory.CreateDirectory(Path.Combine(scanRoot.FullName, ".git"));
        var articlePath = Path.Combine(scanRoot.FullName, "article.md");
        var articleCopyPath = Path.Combine(nested.FullName, "article-copy.md");
        var imagePath = Path.Combine(nested.FullName, "cover.jpg");
        const string originalArticle = "# Private draft";

        await File.WriteAllTextAsync(articlePath, originalArticle);
        await File.WriteAllTextAsync(articleCopyPath, originalArticle);
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);
        await File.WriteAllTextAsync(Path.Combine(ignored.FullName, "config"), "ignored");

        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        var service = new ScanApplicationService(new FileSystemScanner(), repository);
        var fingerprintService = new FingerprintApplicationService(
            new Sha256FileFingerprintService(),
            repository);
        await service.InitializeAsync();

        var firstScan = await service.ScanDirectoryAsync(scanRoot.FullName);
        var firstAssets = await service.ListAssetsAsync();
        var fastFingerprint = await fingerprintService.ProcessPendingAsync(
            FingerprintMode.DuplicateCandidates);
        var duplicateGroups = await service.ListExactDuplicateGroupsAsync();
        var secondScan = await service.ScanDirectoryAsync(scanRoot.FullName);
        var secondAssets = await service.ListAssetsAsync();
        var cachedFastFingerprint = await fingerprintService.ProcessPendingAsync(
            FingerprintMode.DuplicateCandidates);
        var completeFingerprint = await fingerprintService.ProcessPendingAsync(
            FingerprintMode.Complete);
        var cachedCompleteFingerprint = await fingerprintService.ProcessPendingAsync(
            FingerprintMode.Complete);

        Assert.Equal(ScanJobStatus.Completed, firstScan.Status);
        Assert.Equal(3, firstScan.FilesIndexed);
        Assert.Equal(ScanJobStatus.Completed, secondScan.Status);
        Assert.Equal(3, secondScan.FilesIndexed);
        Assert.Equal(3, firstAssets.Count);
        Assert.Equal(3, secondAssets.Count);
        Assert.Equal(
            firstAssets.OrderBy(asset => asset.Path).Select(asset => asset.AssetId),
            secondAssets.OrderBy(asset => asset.Path).Select(asset => asset.AssetId));
        Assert.Equal(originalArticle, await File.ReadAllTextAsync(articlePath));
        var duplicateGroup = Assert.Single(duplicateGroups);
        Assert.Equal(2, duplicateGroup.Assets.Count);
        Assert.Equal(2, fastFingerprint.TotalFiles);
        Assert.Equal(2, fastFingerprint.FingerprintedFiles);
        Assert.Equal(0, fastFingerprint.Errors);
        Assert.Equal(0, cachedFastFingerprint.TotalFiles);
        Assert.Equal(0, cachedFastFingerprint.FingerprintedFiles);
        Assert.Equal(1, completeFingerprint.TotalFiles);
        Assert.Equal(1, completeFingerprint.FingerprintedFiles);
        Assert.Equal(0, completeFingerprint.Errors);
        Assert.Equal(0, cachedCompleteFingerprint.TotalFiles);
        Assert.Equal(0, cachedCompleteFingerprint.FingerprintedFiles);
        Assert.Contains(duplicateGroup.Assets, asset => asset.Path == articlePath);
        Assert.Contains(duplicateGroup.Assets, asset => asset.Path == articleCopyPath);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ProcessPendingAsync_WhenCancelled_ResumesWithoutRehashingCompletedFiles()
    {
        using var directory = new TestDirectory();
        var scanRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "Assets"));
        await File.WriteAllBytesAsync(Path.Combine(scanRoot.FullName, "one.bin"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(scanRoot.FullName, "two.bin"), [4, 5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(scanRoot.FullName, "three.bin"), [7, 8, 9]);

        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        var scanService = new ScanApplicationService(new FileSystemScanner(), repository);
        var fingerprintService = new FingerprintApplicationService(
            new Sha256FileFingerprintService(),
            repository);
        await scanService.InitializeAsync();
        await scanService.ScanDirectoryAsync(scanRoot.FullName);

        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FingerprintProgress>(value =>
        {
            if (value.FingerprintedFiles == 1)
            {
                cancellation.Cancel();
            }
        });

        var cancelled = await fingerprintService.ProcessPendingAsync(
            FingerprintMode.DuplicateCandidates,
            progress,
            cancellation.Token);
        var resumed = await fingerprintService.ProcessPendingAsync(
            FingerprintMode.DuplicateCandidates);
        var cached = await fingerprintService.ProcessPendingAsync(
            FingerprintMode.DuplicateCandidates);

        Assert.True(cancelled.Cancelled);
        Assert.Equal(1, cancelled.FingerprintedFiles);
        Assert.Equal(2, resumed.TotalFiles);
        Assert.Equal(2, resumed.FingerprintedFiles);
        Assert.False(resumed.Cancelled);
        Assert.Equal(0, cached.TotalFiles);

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task MetadataExtraction_WhenOneExtractorFails_RecordsErrorAndContinues()
    {
        using var directory = new TestDirectory();
        var scanRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "Assets"));
        var badPath = Path.Combine(scanRoot.FullName, "broken.bad");
        var notesPath = Path.Combine(scanRoot.FullName, "notes.txt");
        await File.WriteAllBytesAsync(badPath, [1, 2, 3]);
        await File.WriteAllTextAsync(notesPath, "notes");

        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        var scanService = new ScanApplicationService(new FileSystemScanner(), repository);
        var metadataService = new MetadataExtractionApplicationService(
            [
                new FailingMetadataExtractor(),
                new GenericMetadataExtractor()
            ],
            repository);
        await scanService.InitializeAsync();
        await scanService.ScanDirectoryAsync(scanRoot.FullName);

        var summary = await metadataService.ProcessPendingAsync();
        var cached = await metadataService.ProcessPendingAsync();
        var assets = await scanService.ListAssetsAsync();
        var failed = assets.Single(asset => asset.Path == badPath).Metadata;
        var unsupported = assets.Single(asset => asset.Path == notesPath).Metadata;

        Assert.Equal(2, summary.TotalFiles);
        Assert.Equal(0, summary.ExtractedFiles);
        Assert.Equal(1, summary.UnsupportedFiles);
        Assert.Equal(1, summary.Errors);
        Assert.False(summary.Cancelled);
        Assert.Equal(0, cached.TotalFiles);
        Assert.Equal(MetadataExtractionStatus.Error, failed?.Status);
        Assert.Contains("Expected test failure", failed?.ErrorMessage);
        Assert.Equal(MetadataExtractionStatus.Unsupported, unsupported?.Status);
        Assert.Equal("notes", await File.ReadAllTextAsync(notesPath));

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ScanDirectoryAsync_WhenAFileDisappears_MarksItsLocationMissing()
    {
        using var directory = new TestDirectory();
        var scanRoot = Directory.CreateDirectory(Path.Combine(directory.Path, "Assets"));
        var retainedPath = Path.Combine(scanRoot.FullName, "retained.txt");
        var removedPath = Path.Combine(scanRoot.FullName, "removed.txt");
        await File.WriteAllTextAsync(retainedPath, "retained");
        await File.WriteAllTextAsync(removedPath, "removed");

        var repository = new SqliteAssetRepository(
            Path.Combine(directory.Path, "State", "cdsi.db"));
        var service = new ScanApplicationService(new FileSystemScanner(), repository);
        await service.InitializeAsync();
        await service.ScanDirectoryAsync(scanRoot.FullName);

        File.Delete(removedPath);
        await service.ScanDirectoryAsync(scanRoot.FullName);
        var assets = await service.ListAssetsAsync();

        Assert.Equal(
            AssetLocationStatus.Available,
            assets.Single(asset => asset.Path == retainedPath).LocationStatus);
        Assert.Equal(
            AssetLocationStatus.Missing,
            assets.Single(asset => asset.Path == removedPath).LocationStatus);
        Assert.False(File.Exists(removedPath));
        Assert.Equal("retained", await File.ReadAllTextAsync(retainedPath));

        SqliteConnection.ClearAllPools();
    }

    private sealed class FailingMetadataExtractor : IAssetMetadataExtractor
    {
        public string Name => "failing-test";

        public bool Supports(DiscoveredFile file)
        {
            return file.Extension.Equals(".bad", StringComparison.OrdinalIgnoreCase);
        }

        public Task<MetadataExtractionResult> ExtractAsync(
            DiscoveredFile file,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException("Expected test failure.");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Action<T> _report = report;

        public void Report(T value)
        {
            _report(value);
        }
    }
}
