using CDSI.Agent.Core.Abstractions;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Workspaces;

namespace CDSI.Agent.Application.Workspaces;

public sealed class WorkspaceApplicationService
{
    private readonly IAssetRepository _repository;
    private readonly IWorkspaceProvisioner _provisioner;

    public WorkspaceApplicationService(
        IAssetRepository repository,
        IWorkspaceProvisioner provisioner)
    {
        _repository = repository;
        _provisioner = provisioner;
    }

    public async Task<ManagedWorkspace?> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var deviceId = await _repository.GetOrCreateDeviceIdAsync(cancellationToken);
        return await _repository.GetManagedWorkspaceAsync(deviceId, cancellationToken);
    }

    public async Task<WorkspaceConfigurationResult> ConfigureAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var layout = await _provisioner.ProvisionAsync(path, cancellationToken);
        var deviceId = await _repository.GetOrCreateDeviceIdAsync(cancellationToken);
        var previous = await _repository.GetManagedWorkspaceAsync(
            deviceId,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var workspace = await _repository.SaveManagedWorkspaceAsync(
            deviceId,
            layout.RootPath,
            now,
            cancellationToken);

        if (previous is not null &&
            !PathsEqual(previous.InboxPath, layout.InboxPath))
        {
            var oldInboxRoot = (await _repository.ListScanRootsAsync(
                    includeRemoved: false,
                    cancellationToken))
                .FirstOrDefault(root =>
                    root.Mode == ScanRootMode.Managed &&
                    PathsEqual(root.Path, previous.InboxPath));
            if (oldInboxRoot is not null)
            {
                await _repository.RemoveScanRootAsync(
                    oldInboxRoot.Id,
                    now,
                    cancellationToken);
            }
        }

        await _repository.RestoreAssetDirectoryAsync(
            layout.InboxPath,
            cancellationToken);
        await _repository.GetOrCreateScanRootAsync(
            layout.InboxPath,
            ScanRootMode.Managed,
            now,
            cancellationToken);

        return new WorkspaceConfigurationResult(
            workspace,
            layout,
            previous?.Path);
    }

    public static string GetSuggestedDefaultPath()
    {
        const string preferredDrive = @"D:\";
        var parent = OperatingSystem.IsWindows() && Directory.Exists(preferredDrive)
            ? preferredDrive
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(parent, "cdsi_workspace");
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}

public sealed record WorkspaceConfigurationResult(
    ManagedWorkspace Workspace,
    WorkspaceLayout Layout,
    string? PreviousPath);
