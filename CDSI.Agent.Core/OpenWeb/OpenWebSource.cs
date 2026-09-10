namespace CDSI.Agent.Core.OpenWeb;

public sealed record OpenWebSource(
    Guid Id,
    string DisplayName,
    string OriginDomain,
    string WordPressUsername,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static readonly Guid MigratedLegacySourceId =
        Guid.Parse("00000000-0000-0000-0000-000000000001");
}
