namespace CDSI.Agent.Core.Git;

public sealed record GitProfile(
    Guid Id,
    string DisplayName,
    GitHostingProvider Provider,
    string RepositoryUrl,
    string DefaultBranch,
    GitAuthenticationMethod AuthenticationMethod,
    string Username,
    string? SshPublicKeyPath,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
