using CDSI.Agent.Mac.Platform;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Platform;

public sealed class MacLocalVolumeProviderTests
{
    [Fact]
    public async Task MountedVolumesUseUppercaseVolumeUuidAsStableIdentity()
    {
        var volumeUuid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var source = new FakeMountedVolumeSource(
            [
                new MacMountedVolumeCandidate(
                    "/Volumes/Archive",
                    "  Archive  ",
                    " apfs ",
                    DriveType.Removable),
                new MacMountedVolumeCandidate(
                    "/Volumes/Without Identity",
                    "Temporary",
                    "apfs",
                    DriveType.Fixed)
            ],
            new Dictionary<string, Guid>
            {
                ["/Volumes/Archive"] = volumeUuid
            });
        var provider = new MacLocalVolumeProvider(source);

        var volumes = await provider.ListMountedVolumesAsync();

        var volume = Assert.Single(volumes);
        Assert.Equal("00112233-4455-6677-8899-AABBCCDDEEFF", volume.StableId);
        Assert.Equal(volume.StableId, volume.SerialNumber);
        Assert.Equal("/Volumes/Archive/", volume.MountPath);
        Assert.Equal("Archive", volume.Label);
        Assert.Equal("apfs", volume.FileSystem);
        Assert.Equal("Removable", volume.DriveType);
    }

    [Fact]
    public async Task DuplicateUuidUsesTheFirstMountPathInStableOrder()
    {
        var volumeUuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var source = new FakeMountedVolumeSource(
            [
                new MacMountedVolumeCandidate(
                    "/Volumes/Zeta",
                    "Zeta",
                    "apfs",
                    DriveType.Fixed),
                new MacMountedVolumeCandidate(
                    "/Volumes/Alpha",
                    "Alpha",
                    "apfs",
                    DriveType.Fixed)
            ],
            new Dictionary<string, Guid>
            {
                ["/Volumes/Zeta"] = volumeUuid,
                ["/Volumes/Alpha"] = volumeUuid
            });
        var provider = new MacLocalVolumeProvider(source);

        var volume = Assert.Single(await provider.ListMountedVolumesAsync());

        Assert.Equal("/Volumes/Alpha/", volume.MountPath);
        Assert.Equal("Alpha", volume.Label);
    }

    [Fact]
    public async Task UnsupportedPlatformReturnsNoVolumesWithoutEnumerating()
    {
        var source = new FakeMountedVolumeSource(
            [],
            new Dictionary<string, Guid>())
        {
            IsSupported = false
        };
        var provider = new MacLocalVolumeProvider(source);

        var volumes = await provider.ListMountedVolumesAsync();

        Assert.Empty(volumes);
        Assert.False(source.WasEnumerated);
    }

    [Fact]
    public void VolumeUuidIsParsedUsingNetworkByteOrder()
    {
        byte[] uuidBytes =
        [
            0x00, 0x11, 0x22, 0x33,
            0x44, 0x55,
            0x66, 0x77,
            0x88, 0x99,
            0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff
        ];

        var uuid = MacVolumeUuid.ParseNetworkOrderUuid(uuidBytes);

        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", uuid.ToString("D"));
    }

    private sealed class FakeMountedVolumeSource : IMacMountedVolumeSource
    {
        private readonly IReadOnlyList<MacMountedVolumeCandidate> _volumes;
        private readonly IReadOnlyDictionary<string, Guid> _uuids;

        public FakeMountedVolumeSource(
            IReadOnlyList<MacMountedVolumeCandidate> volumes,
            IReadOnlyDictionary<string, Guid> uuids)
        {
            _volumes = volumes;
            _uuids = uuids;
        }

        public bool IsSupported { get; init; } = true;

        public bool WasEnumerated { get; private set; }

        public IReadOnlyList<MacMountedVolumeCandidate> ListMountedVolumes(
            CancellationToken cancellationToken)
        {
            WasEnumerated = true;
            cancellationToken.ThrowIfCancellationRequested();
            return _volumes;
        }

        public bool TryGetVolumeUuid(string mountPath, out Guid volumeUuid) =>
            _uuids.TryGetValue(mountPath, out volumeUuid);
    }
}
