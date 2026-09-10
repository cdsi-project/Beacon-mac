namespace CDSI.Agent.Core.OpenWeb;

public sealed class OpenWebConnection
{
    public OpenWebConnection(
        string originDomain,
        string username,
        string applicationPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPassword);
        OriginDomain = originDomain;
        Username = username;
        ApplicationPassword = applicationPassword;
    }

    public string OriginDomain { get; }

    public string Username { get; }

    public string ApplicationPassword { get; }

    public override string ToString()
    {
        return $"{nameof(OpenWebConnection)} {{ OriginDomain = {OriginDomain}, Username = {Username}, ApplicationPassword = *** }}";
    }
}
