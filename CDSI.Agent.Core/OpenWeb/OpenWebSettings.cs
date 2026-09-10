using System.Globalization;

namespace CDSI.Agent.Core.OpenWeb;

public static class OpenWebOriginDomain
{
    private const int MaximumDomainLength = 253;

    public static bool TryNormalize(
        string? value,
        out string? normalizedDomain,
        out string? errorMessage)
    {
        normalizedDomain = null;
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var candidate = value.Trim();
        if (candidate.Contains("://", StringComparison.Ordinal) ||
            candidate.IndexOfAny(['/', '\\', ':', '?', '#', '@']) >= 0)
        {
            errorMessage = "源站域名只能填写域名，不能包含协议、端口、路径或账号信息。";
            return false;
        }

        if (candidate.EndsWith(".", StringComparison.Ordinal))
        {
            candidate = candidate[..^1];
        }

        string asciiDomain;
        try
        {
            asciiDomain = new IdnMapping()
                .GetAscii(candidate)
                .ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            errorMessage = "源站域名格式无效。";
            return false;
        }

        var labels = asciiDomain.Split('.');
        if (asciiDomain.Length is 0 or > MaximumDomainLength ||
            labels.Any(label =>
                label.Length is 0 or > 63 ||
                label[0] == '-' ||
                label[^1] == '-' ||
                label.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character != '-')) ||
            Uri.CheckHostName(asciiDomain) != UriHostNameType.Dns)
        {
            errorMessage = "源站域名格式无效。";
            return false;
        }

        normalizedDomain = asciiDomain;
        return true;
    }
}
