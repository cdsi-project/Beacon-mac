using System.Diagnostics;

namespace CDSI.Agent.Mac.Settings;

internal static class SshKeySupport
{
    private static readonly string[] PreferredKeyNames =
    [
        "id_ed25519",
        "id_ecdsa",
        "id_rsa",
        "id_ed25519_beacon",
        "id_ed25519_atlas"
    ];

    internal static string GetDefaultSshDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh");
    }

    internal static SshKeyPairPaths? FindDefaultKeyPair(string sshDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshDirectory);
        var directory = Path.GetFullPath(sshDirectory);
        foreach (var keyName in PreferredKeyNames)
        {
            var privateKeyPath = Path.Combine(directory, keyName);
            var publicKeyPath = privateKeyPath + ".pub";
            if (File.Exists(privateKeyPath) && File.Exists(publicKeyPath))
            {
                return new SshKeyPairPaths(publicKeyPath, privateKeyPath);
            }
        }

        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var publicKeyPath in Directory.EnumerateFiles(directory, "*.pub")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var privateKeyPath = publicKeyPath[..^4];
            if (File.Exists(privateKeyPath))
            {
                return new SshKeyPairPaths(publicKeyPath, privateKeyPath);
            }
        }

        return null;
    }

    internal static SshKeyPairPaths CreateUnusedKeyPairPaths(string sshDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshDirectory);
        var directory = Path.GetFullPath(sshDirectory);
        const string baseName = "id_ed25519_beacon";
        for (var suffix = 1; ; suffix++)
        {
            var keyName = suffix == 1 ? baseName : $"{baseName}_{suffix}";
            var privateKeyPath = Path.Combine(directory, keyName);
            var publicKeyPath = privateKeyPath + ".pub";
            if (!File.Exists(privateKeyPath) && !File.Exists(publicKeyPath))
            {
                return new SshKeyPairPaths(publicKeyPath, privateKeyPath);
            }
        }
    }

    internal static bool IsUsablePublicKeyPath(string? publicKeyPath)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPath))
        {
            return false;
        }

        try
        {
            var normalizedPath = Path.GetFullPath(publicKeyPath);
            return normalizedPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(normalizedPath) &&
                File.Exists(normalizedPath[..^4]);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static ProcessStartInfo CreateSshKeyGenerationStartInfo(
        string? comment,
        string privateKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);
        var normalizedPrivateKeyPath = Path.GetFullPath(privateKeyPath);
        if (File.Exists(normalizedPrivateKeyPath) ||
            File.Exists(normalizedPrivateKeyPath + ".pub"))
        {
            throw new IOException("SSH 密钥目标文件已存在，不能覆盖。请重新生成路径。");
        }

        var normalizedComment = string.IsNullOrWhiteSpace(comment)
            ? $"{Environment.UserName}@{Environment.MachineName}"
            : comment.Trim();
        var quotedPrivateKeyPath = QuoteForPosixShell(normalizedPrivateKeyPath);
        var command = string.Join(
            ' ',
            "/usr/bin/ssh-keygen",
            "-t ed25519",
            "-C",
            QuoteForPosixShell(normalizedComment),
            "-f",
            quotedPrivateKeyPath,
            "&& /usr/bin/ssh-add --apple-use-keychain",
            quotedPrivateKeyPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("on run argv");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("tell application \"Terminal\"");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("activate");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("do script (item 1 of argv)");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("end tell");
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("end run");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static string QuoteForPosixShell(string value)
    {
        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }
}

internal sealed record SshKeyPairPaths(
    string PublicKeyPath,
    string PrivateKeyPath);
