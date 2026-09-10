using CDSI.Agent.Core.Workspaces;

namespace CDSI.Agent.Core.Abstractions;

public interface IWorkspaceProvisioner
{
    string NormalizeAndValidatePath(string path);

    Task<WorkspaceLayout> ProvisionAsync(
        string path,
        CancellationToken cancellationToken = default);
}
