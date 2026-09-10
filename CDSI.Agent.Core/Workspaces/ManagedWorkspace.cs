namespace CDSI.Agent.Core.Workspaces;

public sealed record ManagedWorkspace(
    Guid Id,
    string DeviceId,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string InboxPath => System.IO.Path.Combine(Path, "Inbox");
}
