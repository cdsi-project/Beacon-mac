using CDSI.Agent.Mac.Settings;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Settings;

public sealed class SshKeySupportTests
{
    [Fact]
    public void FindDefaultKeyPairRequiresBothPublicAndPrivateFiles()
    {
        var testRoot = CreateTestRoot();
        try
        {
            File.WriteAllText(Path.Combine(testRoot, "id_ed25519.pub"), "public");
            Assert.Null(SshKeySupport.FindDefaultKeyPair(testRoot));

            File.WriteAllText(Path.Combine(testRoot, "id_ed25519"), "private");
            var pair = Assert.IsType<SshKeyPairPaths>(
                SshKeySupport.FindDefaultKeyPair(testRoot));

            Assert.Equal(Path.Combine(testRoot, "id_ed25519.pub"), pair.PublicKeyPath);
            Assert.Equal(Path.Combine(testRoot, "id_ed25519"), pair.PrivateKeyPath);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateUnusedKeyPairPathsNeverSelectsAnOccupiedHalfPair()
    {
        var testRoot = CreateTestRoot();
        try
        {
            File.WriteAllText(Path.Combine(testRoot, "id_ed25519_beacon.pub"), "public");
            File.WriteAllText(Path.Combine(testRoot, "id_ed25519_beacon_2"), "private");

            var pair = SshKeySupport.CreateUnusedKeyPairPaths(testRoot);

            Assert.Equal(
                Path.Combine(testRoot, "id_ed25519_beacon_3"),
                pair.PrivateKeyPath);
            Assert.Equal(pair.PrivateKeyPath + ".pub", pair.PublicKeyPath);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void GenerationStartInfoUsesTerminalAndQuotesUntrustedValues()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var privateKey = Path.Combine(testRoot, "creator's key");

            var startInfo = SshKeySupport.CreateSshKeyGenerationStartInfo(
                "creator's account; touch /tmp/should-not-run",
                privateKey);

            Assert.Equal("/usr/bin/osascript", startInfo.FileName);
            Assert.False(startInfo.UseShellExecute);
            var command = startInfo.ArgumentList.Last();
            Assert.Contains("/usr/bin/ssh-keygen -t ed25519", command);
            Assert.Contains("&& /usr/bin/ssh-add --apple-use-keychain", command);
            Assert.Contains("'creator'\\''s account; touch /tmp/should-not-run'", command);
            Assert.Contains(
                $"'{privateKey.Replace("'", "'\\''", StringComparison.Ordinal)}'",
                command);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void GenerationStartInfoRejectsExistingKeyMaterial()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var privateKey = Path.Combine(testRoot, "id_ed25519_beacon");
            File.WriteAllText(privateKey + ".pub", "public");

            Assert.Throws<IOException>(() =>
                SshKeySupport.CreateSshKeyGenerationStartInfo(null, privateKey));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static string CreateTestRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "cdsi-agent-mac-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
