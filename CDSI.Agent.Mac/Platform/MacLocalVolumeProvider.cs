using System.Runtime.InteropServices;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Mac.Platform;

public sealed class MacLocalVolumeProvider : ILocalVolumeProvider
{
    private readonly IMacMountedVolumeSource _volumeSource;

    public MacLocalVolumeProvider()
        : this(new DriveInfoMacMountedVolumeSource())
    {
    }

    internal MacLocalVolumeProvider(IMacMountedVolumeSource volumeSource)
    {
        _volumeSource = volumeSource ??
            throw new ArgumentNullException(nameof(volumeSource));
    }

    public Task<IReadOnlyList<LocalVolumeDescriptor>> ListMountedVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_volumeSource.IsSupported)
        {
            return Task.FromResult<IReadOnlyList<LocalVolumeDescriptor>>([]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var volumes = new Dictionary<string, LocalVolumeDescriptor>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in _volumeSource
            .ListMountedVolumes(cancellationToken)
            .OrderBy(volume => volume.MountPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_volumeSource.TryGetVolumeUuid(
                    candidate.MountPath,
                    out var volumeUuid))
            {
                continue;
            }

            var stableId = volumeUuid.ToString("D").ToUpperInvariant();
            volumes.TryAdd(
                stableId,
                new LocalVolumeDescriptor(
                    stableId,
                    stableId,
                    EnsureTrailingSeparator(candidate.MountPath),
                    NullIfWhiteSpace(candidate.Label),
                    NullIfWhiteSpace(candidate.FileSystem),
                    candidate.DriveType.ToString()));
        }

        return Task.FromResult<IReadOnlyList<LocalVolumeDescriptor>>(
            volumes.Values.ToArray());
    }

    internal static string EnsureTrailingSeparator(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record MacMountedVolumeCandidate(
    string MountPath,
    string? Label,
    string? FileSystem,
    DriveType DriveType);

internal interface IMacMountedVolumeSource
{
    bool IsSupported { get; }

    IReadOnlyList<MacMountedVolumeCandidate> ListMountedVolumes(
        CancellationToken cancellationToken);

    bool TryGetVolumeUuid(string mountPath, out Guid volumeUuid);
}

internal sealed class DriveInfoMacMountedVolumeSource : IMacMountedVolumeSource
{
    public bool IsSupported => OperatingSystem.IsMacOS();

    public IReadOnlyList<MacMountedVolumeCandidate> ListMountedVolumes(
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return [];
        }

        var candidates = new List<MacMountedVolumeCandidate>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                candidates.Add(new MacMountedVolumeCandidate(
                    drive.RootDirectory.FullName,
                    drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.DriveType));
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // A removable volume may disappear while it is being inspected.
            }
        }

        return candidates;
    }

    public bool TryGetVolumeUuid(string mountPath, out Guid volumeUuid) =>
        MacVolumeUuid.TryRead(mountPath, out volumeUuid);
}

internal static class MacVolumeUuid
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const ushort AttributeBitmapCount = 5;
    private const uint AttributeVolumeUuid = 0x00040000;
    private const uint AttributeVolumeInfo = 0x80000000;
    private const int ResultLengthSize = sizeof(uint);
    private const int UuidSize = 16;

    internal static bool TryRead(string mountPath, out Guid volumeUuid)
    {
        volumeUuid = Guid.Empty;
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        var attributes = new AttributeList
        {
            BitmapCount = AttributeBitmapCount,
            VolumeAttributes = AttributeVolumeInfo | AttributeVolumeUuid
        };
        var buffer = new byte[ResultLengthSize + UuidSize];
        if (GetAttributeList(
                mountPath,
                ref attributes,
                buffer,
                (nuint)buffer.Length,
                0) != 0)
        {
            return false;
        }

        var returnedLength = BitConverter.ToUInt32(buffer, 0);
        if (returnedLength < (uint)buffer.Length)
        {
            return false;
        }

        volumeUuid = ParseNetworkOrderUuid(
            buffer.AsSpan(ResultLengthSize, UuidSize));
        return volumeUuid != Guid.Empty;
    }

    internal static Guid ParseNetworkOrderUuid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != UuidSize)
        {
            throw new ArgumentException(
                "A macOS volume UUID must contain exactly 16 bytes.",
                nameof(bytes));
        }

        return new Guid(bytes, bigEndian: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint CommonAttributes;
        public uint VolumeAttributes;
        public uint DirectoryAttributes;
        public uint FileAttributes;
        public uint ForkAttributes;
    }

    [DllImport(
        LibSystem,
        EntryPoint = "getattrlist",
        SetLastError = true)]
    private static extern int GetAttributeList(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref AttributeList attributeList,
        [Out] byte[] attributeBuffer,
        nuint attributeBufferSize,
        uint options);
}
