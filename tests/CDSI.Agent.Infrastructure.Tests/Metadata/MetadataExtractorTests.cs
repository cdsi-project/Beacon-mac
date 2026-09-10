using CDSI.Agent.Core.Metadata;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Infrastructure.Metadata;

namespace CDSI.Agent.Infrastructure.Tests.Metadata;

public sealed class MetadataExtractorTests
{
    [Fact]
    public async Task TagLibExtractor_ExtractsPngDimensions()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "pixel.png");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZlN0AAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(path, png);
        var file = CreateDiscoveredFile(path, "image/png");
        var extractor = new TagLibMetadataExtractor();

        var result = await extractor.ExtractAsync(file);

        Assert.True(extractor.Supports(file));
        Assert.Equal(MetadataExtractionStatus.Extracted, result.Status);
        Assert.Equal(AssetMediaKind.Image, result.Content?.Kind);
        Assert.Equal(1, result.Content?.Width);
        Assert.Equal(1, result.Content?.Height);
        Assert.Equal(png, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task GenericExtractor_ReturnsUnsupportedWithoutChangingTheFile()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "notes.unknown");
        const string content = "private notes";
        await File.WriteAllTextAsync(path, content);
        var file = CreateDiscoveredFile(path, null);
        var extractor = new GenericMetadataExtractor();

        var result = await extractor.ExtractAsync(file);

        Assert.True(extractor.Supports(file));
        Assert.Equal(MetadataExtractionStatus.Unsupported, result.Status);
        Assert.Null(result.Content);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    private static DiscoveredFile CreateDiscoveredFile(
        string path,
        string? mimeType)
    {
        var info = new FileInfo(path);
        return new DiscoveredFile(
            info.FullName,
            info.Name,
            info.Extension.ToLowerInvariant(),
            mimeType,
            info.Length,
            new DateTimeOffset(info.CreationTimeUtc),
            new DateTimeOffset(info.LastWriteTimeUtc));
    }
}
