namespace CDSI.Agent.Core.Identity;

public sealed record ClientIdentity(
    Guid Id,
    DateTimeOffset CreatedAtUtc)
{
    public string Value => Id.ToString("D");
}
