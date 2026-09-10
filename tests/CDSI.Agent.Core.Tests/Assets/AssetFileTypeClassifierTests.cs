using CDSI.Agent.Core.Assets;

namespace CDSI.Agent.Core.Tests.Assets;

public sealed class AssetFileTypeClassifierTests
{
    [Theory]
    [InlineData(".mp4", "video/mp4", AssetFileTypeFilter.Video)]
    [InlineData(".mp3", "audio/mpeg", AssetFileTypeFilter.Audio)]
    [InlineData(".jpg", "image/jpeg", AssetFileTypeFilter.Image)]
    [InlineData(".json", "application/json", AssetFileTypeFilter.Document)]
    [InlineData("TXT", null, AssetFileTypeFilter.Document)]
    [InlineData(".zip", "application/zip", AssetFileTypeFilter.Other)]
    public void Classify_UsesTheSameFileTypeGroupsAsAssetFiltering(
        string extension,
        string? mimeType,
        AssetFileTypeFilter expected)
    {
        Assert.Equal(expected, AssetFileTypeClassifier.Classify(extension, mimeType));
        Assert.True(AssetFileTypeClassifier.Matches(extension, mimeType, expected));
        Assert.True(AssetFileTypeClassifier.Matches(
            extension,
            mimeType,
            AssetFileTypeFilter.All));
    }

    [Fact]
    public void Matches_RejectsAnInvalidFilter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AssetFileTypeClassifier.Matches(".mp4", "video/mp4", (AssetFileTypeFilter)99));
    }
}
