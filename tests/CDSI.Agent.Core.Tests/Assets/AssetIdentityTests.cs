using CDSI.Agent.Core.Assets;

namespace CDSI.Agent.Core.Tests.Assets;

public sealed class AssetIdentityTests
{
    [Fact]
    public void AssetIdentity_IsIndependentFromPhysicalLocations()
    {
        var assetId = Guid.NewGuid();
        var asset = new Asset(
            assetId,
            "cover.psd",
            "application/octet-stream",
            ".psd",
            128,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            AssetStatus.Indexed);

        var localLocation = new AssetLocation(
            Guid.NewGuid(),
            assetId,
            AssetLocationType.Local,
            AssetLocationOwnership.External,
            "device-1",
            @"D:\Creator\cover.psd",
            AssetLocationStatus.Available,
            DateTimeOffset.UtcNow,
            null);
        var backupLocation = new AssetLocation(
            Guid.NewGuid(),
            assetId,
            AssetLocationType.ObjectStorage,
            AssetLocationOwnership.Managed,
            "storage-1",
            "assets/cover.psd",
            AssetLocationStatus.Available,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        Assert.Equal(asset.Id, localLocation.AssetId);
        Assert.Equal(asset.Id, backupLocation.AssetId);
        Assert.NotEqual(localLocation.Path, backupLocation.Path);
    }
}
