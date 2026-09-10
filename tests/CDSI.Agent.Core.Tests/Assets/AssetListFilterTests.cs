using CDSI.Agent.Core.Assets;

namespace CDSI.Agent.Core.Tests.Assets;

public sealed class AssetListFilterTests
{
    [Fact]
    public void Empty_HasNoActiveConditions()
    {
        Assert.True(AssetListFilter.Empty.IsEmpty);
    }

    [Fact]
    public void Constructor_RejectsAnInvalidCreationTimeRange()
    {
        var boundary = DateTimeOffset.Parse("2026-08-20T00:00:00Z");

        Assert.Throws<ArgumentException>(() => new AssetListFilter(
            AssetFileTypeFilter.All,
            boundary,
            boundary));
    }

    [Fact]
    public void Constructor_RecognizesCombinedConditions()
    {
        var filter = new AssetListFilter(
            AssetFileTypeFilter.Video,
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        Assert.False(filter.IsEmpty);
        Assert.Equal(AssetFileTypeFilter.Video, filter.FileType);
    }

    [Theory]
    [InlineData("MP4", ".mp4")]
    [InlineData(" .JPG ", ".jpg")]
    public void Constructor_NormalizesExtension(string input, string expected)
    {
        var filter = new AssetListFilter(extension: input);

        Assert.Equal(expected, filter.Extension);
        Assert.False(filter.IsEmpty);
    }

    [Fact]
    public void Constructor_RejectsAnExtensionContainingAPath()
    {
        Assert.Throws<ArgumentException>(() =>
            new AssetListFilter(extension: "folder/file.txt"));
    }

    [Fact]
    public void Constructor_RecognizesATagCondition()
    {
        var tagId = Guid.NewGuid();

        var filter = new AssetListFilter(tagId: tagId);

        Assert.False(filter.IsEmpty);
        Assert.Equal(tagId, filter.TagId);
    }

    [Theory]
    [InlineData("  Final Cut  ", "Final Cut")]
    [InlineData("素材_01.MP4", "素材_01.MP4")]
    public void Constructor_NormalizesFilenameSearch(
        string input,
        string expected)
    {
        var filter = new AssetListFilter(filenameContains: input);

        Assert.Equal(expected, filter.FilenameContains);
        Assert.False(filter.IsEmpty);
    }

    [Fact]
    public void Constructor_RejectsAnOversizedFilenameSearch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AssetListFilter(filenameContains: new string('a', 256)));
    }
}
