using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Core.Tests.Storage;

public sealed class ObjectStorageObjectKeyTests
{
    [Fact]
    public void TryRenameFile_PreservesTheExistingDirectory()
    {
        var success = ObjectStorageObjectKey.TryRenameFile(
            "项目一/原文件.mp4",
            "成片.mp4",
            out var objectKey,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.Equal("项目一/成片.mp4", objectKey);
    }

    [Theory]
    [InlineData("folder/name.mp4")]
    [InlineData("folder\\name.mp4")]
    [InlineData("..")]
    public void TryRenameFile_RejectsAPathInsteadOfAFilename(string filename)
    {
        var success = ObjectStorageObjectKey.TryRenameFile(
            "项目一/原文件.mp4",
            filename,
            out _,
            out var errorMessage);

        Assert.False(success);
        Assert.NotNull(errorMessage);
    }

    [Fact]
    public void TryCreateForAsset_PreservesTheRequestedUnicodeFilename()
    {
        var assetId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        var success = ObjectStorageObjectKey.TryCreateForAsset(
            assetId,
            "  成片-最终版.mp4  ",
            out var objectKey,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.Equal(
            "assets/00112233445566778899aabbccddeeff/  成片-最终版.mp4  ",
            objectKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("folder/file.mp4")]
    [InlineData("folder\\file.mp4")]
    [InlineData("line\nbreak.mp4")]
    public void TryCreateForAsset_RejectsValuesThatAreNotSingleFilenames(
        string filename)
    {
        var success = ObjectStorageObjectKey.TryCreateForAsset(
            Guid.NewGuid(),
            filename,
            out var objectKey,
            out var errorMessage);

        Assert.False(success);
        Assert.Empty(objectKey);
        Assert.False(string.IsNullOrWhiteSpace(errorMessage));
    }

    [Fact]
    public void TryCreateForAsset_RejectsAnObjectKeyOverTheOssLimit()
    {
        var success = ObjectStorageObjectKey.TryCreateForAsset(
            Guid.NewGuid(),
            new string('界', 400),
            out _,
            out var errorMessage);

        Assert.False(success);
        Assert.Equal("OSS 文件名过长。", errorMessage);
    }

    [Fact]
    public void TryCreateForDirectory_PreservesCollectionAndOriginalFilename()
    {
        var success = ObjectStorageObjectKey.TryCreateForDirectory(
            "第一期视频",
            "成片-最终版.mp4",
            out var objectKey,
            out var errorMessage);

        Assert.True(success);
        Assert.Null(errorMessage);
        Assert.Equal("第一期视频/成片-最终版.mp4", objectKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("项目/子目录")]
    [InlineData("项目\\子目录")]
    [InlineData("项目\n目录")]
    public void TryCreateForDirectory_RejectsInvalidDirectoryNames(string directoryName)
    {
        var success = ObjectStorageObjectKey.TryCreateForDirectory(
            directoryName,
            "asset.mp4",
            out var objectKey,
            out var errorMessage);

        Assert.False(success);
        Assert.Empty(objectKey);
        Assert.Contains("OSS 目录名", errorMessage);
    }
}
