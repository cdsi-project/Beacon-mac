namespace CDSI.Agent.Core.Workspaces;

public sealed record WorkspaceLayout(
    string RootPath,
    string InboxPath,
    string AssetsPath,
    string ExportsPath,
    string CachePath,
    string TempPath,
    string SystemPath);
