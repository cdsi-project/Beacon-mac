namespace CDSI.Agent.Core.Git;

public static class GitRepositoryAddress
{
    public static bool TryNormalize(
        GitHostingProvider provider,
        string? value,
        out string? normalized,
        out string? errorMessage)
    {
        normalized = null;
        errorMessage = null;
        if (!Enum.IsDefined(provider))
        {
            errorMessage = "不支持该 Git 托管平台。";
            return false;
        }

        var input = value?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "必须填写 Git 仓库地址。";
            return false;
        }

        var expectedHost = provider == GitHostingProvider.GitHub
            ? "github.com"
            : "gitee.com";
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "ssh")
        {
            if (!string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = $"所选平台的仓库域名必须是 {expectedHost}。";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                errorMessage = "Git 仓库地址不能包含查询参数或片段。";
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.UserInfo))
            {
                errorMessage = "仓库地址中不能包含账号或密码等凭据。";
                return false;
            }

            if (uri.Scheme == "ssh" &&
                !string.IsNullOrEmpty(uri.UserInfo) &&
                !string.Equals(uri.UserInfo, "git", StringComparison.Ordinal))
            {
                errorMessage = "SSH 仓库地址只支持 git 用户，不能嵌入密码。";
                return false;
            }

            var repositoryPath = NormalizeRepositoryPath(uri.AbsolutePath);
            if (repositoryPath is null)
            {
                errorMessage = "仓库地址必须包含所有者和仓库名称。";
                return false;
            }

            normalized = uri.Scheme == Uri.UriSchemeHttps
                ? $"https://{expectedHost}/{repositoryPath}"
                : $"ssh://git@{expectedHost}/{repositoryPath}";
            return true;
        }

        var scpPrefix = $"git@{expectedHost}:";
        if (input.StartsWith(scpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var repositoryPath = NormalizeRepositoryPath(input[scpPrefix.Length..]);
            if (repositoryPath is null)
            {
                errorMessage = "仓库地址必须包含所有者和仓库名称。";
                return false;
            }

            normalized = $"git@{expectedHost}:{repositoryPath}";
            return true;
        }

        errorMessage =
            $"请输入 {expectedHost} 的 HTTPS 或 SSH 仓库地址。";
        return false;
    }

    private static string? NormalizeRepositoryPath(string path)
    {
        var normalized = path.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2 &&
            segments.All(segment => segment is not "." and not "..")
                ? string.Join('/', segments)
                : null;
    }
}
