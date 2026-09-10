using System.Runtime.InteropServices;
using System.Text;
using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Scanning;

namespace CDSI.Agent.Infrastructure.FileSystem;

public sealed class WindowsLocalVolumeProvider : ILocalVolumeProvider
{
    private const int MaximumVolumeNameLength = 1024;
    private const int MaximumLabelLength = 261;

    public Task<IReadOnlyList<LocalVolumeDescriptor>> ListMountedVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<LocalVolumeDescriptor>>([]);
        }

        var volumes = new Dictionary<string, LocalVolumeDescriptor>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives()
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            try
            {
                if (!drive.IsReady || !TryReadVolume(drive, out var volume))
                {
                    continue;
                }

                volumes.TryAdd(volume.StableId, volume);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                // A removable volume may disappear while Windows is enumerating it.
            }
        }

        return Task.FromResult<IReadOnlyList<LocalVolumeDescriptor>>(
            volumes.Values.ToArray());
    }

    private static bool TryReadVolume(
        DriveInfo drive,
        out LocalVolumeDescriptor volume)
    {
        var mountPath = EnsureTrailingSeparator(
            Path.GetFullPath(drive.RootDirectory.FullName));
        var volumeName = new StringBuilder(MaximumVolumeNameLength);
        if (!GetVolumeNameForVolumeMountPoint(
                mountPath,
                volumeName,
                volumeName.Capacity))
        {
            volume = null!;
            return false;
        }

        var label = new StringBuilder(MaximumLabelLength);
        var fileSystem = new StringBuilder(MaximumLabelLength);
        if (!GetVolumeInformation(
                mountPath,
                label,
                label.Capacity,
                out var serialNumber,
                out _,
                out _,
                fileSystem,
                fileSystem.Capacity))
        {
            volume = null!;
            return false;
        }

        volume = new LocalVolumeDescriptor(
            volumeName.ToString().TrimEnd('\\').ToUpperInvariant(),
            serialNumber.ToString("X8"),
            mountPath,
            NullIfEmpty(label.ToString()),
            NullIfEmpty(fileSystem.ToString()),
            drive.DriveType.ToString());
        return true;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string? NullIfEmpty(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);
}
