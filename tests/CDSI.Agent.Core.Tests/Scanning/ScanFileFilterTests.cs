using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Core.Tests.Scanning;

public sealed class ScanFileFilterTests
{
    [Fact]
    public void Constructor_NormalizesSortsAndDeduplicatesTheWhitelist()
    {
        var filter = new ScanFileFilter(
            AssetFileTypeFilter.All,
            ["MOV", "*.mp4", ".MP4"]);

        Assert.True(filter.UsesExtensionWhitelist);
        Assert.Equal([".mov", ".mp4"], filter.ExtensionWhitelist);
        Assert.True(filter.Matches(".MOV", "video/quicktime"));
        Assert.False(filter.Matches(".avi", "video/x-msvideo"));
        Assert.False(filter.Matches(null, "video/mp4"));
    }

    [Fact]
    public void Constructor_RejectsInvalidExtensions()
    {
        Assert.Throws<ArgumentException>(() =>
            new ScanFileFilter(
                AssetFileTypeFilter.All,
                ["../mp4"]));
    }

    [Fact]
    public void EmptyWhitelist_UsesTheConfiguredFileTypeCategory()
    {
        var filter = new ScanFileFilter(AssetFileTypeFilter.Video);

        Assert.False(filter.UsesExtensionWhitelist);
        Assert.True(filter.Matches(".mp4", "video/mp4"));
        Assert.False(filter.Matches(".txt", "text/plain"));
    }

    [Fact]
    public void MultipleStrategies_MatchAnySelectedCategoryOrExtension()
    {
        var filter = new ScanFileFilter(
            [AssetFileTypeFilter.Video, AssetFileTypeFilter.Image],
            ["PSD"]);

        Assert.Equal(
            [AssetFileTypeFilter.Video, AssetFileTypeFilter.Image],
            filter.FileTypeFilters);
        Assert.True(filter.Matches(".mp4", "video/mp4"));
        Assert.True(filter.Matches(".png", "image/png"));
        Assert.True(filter.Matches(".psd", "application/octet-stream"));
        Assert.False(filter.Matches(".mp3", "audio/mpeg"));
        Assert.False(filter.Matches(".txt", "text/plain"));
    }

    [Fact]
    public void MultipleStrategies_RequireAtLeastOneSelection()
    {
        Assert.Throws<ArgumentException>(() =>
            new ScanFileFilter([], []));
    }
}
