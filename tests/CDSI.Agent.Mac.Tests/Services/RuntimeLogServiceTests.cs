using CDSI.Agent.Mac.Services;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Services;

public sealed class RuntimeLogServiceTests
{
    [Fact]
    public void RedactSensitiveText_RemovesAssignmentsAndUrlQueries()
    {
        const string input =
            "AccessKeySecret=very-secret password: hunter2 token = abc123 " +
            "signature=signed-value https://example.com/article?token=hidden&draft=1";

        var redacted = RuntimeLogService.RedactSensitiveText(input);

        Assert.Contains("AccessKeySecret=[REDACTED]", redacted);
        Assert.Contains("password: [REDACTED]", redacted);
        Assert.Contains("token = [REDACTED]", redacted);
        Assert.Contains("signature=[REDACTED]", redacted);
        Assert.Contains("https://example.com/article?[REDACTED]", redacted);
        Assert.DoesNotContain("very-secret", redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("signed-value", redacted);
        Assert.DoesNotContain("token=hidden", redacted);
    }

    [Fact]
    public void RedactSensitiveText_RemovesCommonHeadersCloudSecretsAndUrlUserInfo()
    {
        const string input =
            "Authorization: Bearer auth-token API_KEY=api-secret " +
            "secretAccessKey='cloud secret' password=\"two words\" " +
            "HTTPS://creator:embedded-password@example.com/object?Signature=query-secret";

        var redacted = RuntimeLogService.RedactSensitiveText(input);

        Assert.Contains("Authorization: [REDACTED]", redacted);
        Assert.Contains("API_KEY=[REDACTED]", redacted);
        Assert.Contains("secretAccessKey=[REDACTED]", redacted);
        Assert.Contains("password=[REDACTED]", redacted);
        Assert.Contains("HTTPS://[REDACTED]@example.com/object?[REDACTED]", redacted);
        Assert.DoesNotContain("auth-token", redacted);
        Assert.DoesNotContain("api-secret", redacted);
        Assert.DoesNotContain("cloud secret", redacted);
        Assert.DoesNotContain("two words", redacted);
        Assert.DoesNotContain("embedded-password", redacted);
        Assert.DoesNotContain("query-secret", redacted);
    }

    [Fact]
    public void WriteError_PersistsOnlyRedactedDiagnosticText()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var service = new RuntimeLogService(testRoot);

            service.WriteError(
                "backup failed: token=message-secret",
                new InvalidOperationException(
                    "AccessKeySecret=exception-secret " +
                    "https://example.com/object?signature=query-secret"));

            var content = service.ReadRecent();
            Assert.Contains("[ERROR]", content);
            Assert.Contains("backup failed", content);
            Assert.Contains("token=[REDACTED]", content);
            Assert.Contains("AccessKeySecret=[REDACTED]", content);
            Assert.Contains("https://example.com/object?[REDACTED]", content);
            Assert.DoesNotContain("message-secret", content);
            Assert.DoesNotContain("exception-secret", content);
            Assert.DoesNotContain("query-secret", content);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void WriteMethods_DoNotThrowWhenTheLogDirectoryBecomesUnavailable()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var service = new RuntimeLogService(testRoot);
            Directory.Delete(service.LogDirectory);
            File.WriteAllText(service.LogDirectory, "blocks directory recreation");

            var informationError = Record.Exception(() =>
                service.WriteInformation("diagnostic information"));
            var errorError = Record.Exception(() =>
                service.WriteError("diagnostic error", new InvalidOperationException()));

            Assert.Null(informationError);
            Assert.Null(errorError);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void TryUseWorkspaceCopiesTheCurrentSessionAndContinuesInWorkspaceLogs()
    {
        var testRoot = CreateTestRoot();
        var workspace = Path.Combine(testRoot, "workspace");
        try
        {
            var service = new RuntimeLogService(Path.Combine(testRoot, "data"));
            service.WriteInformation("before workspace");

            var switched = service.TryUseWorkspace(workspace);
            service.WriteInformation("after workspace");

            Assert.True(switched);
            Assert.Equal(
                Path.Combine(Path.GetFullPath(workspace), "System", "Logs"),
                service.LogDirectory);
            Assert.StartsWith(service.LogDirectory, service.CurrentLogPath);
            var content = service.ReadRecent();
            Assert.Contains("before workspace", content);
            Assert.Contains("after workspace", content);
            Assert.Single(service.GetLogFiles());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void SwitchingBetweenWorkspacesStartsANewLogWithoutCopyingTheOldWorkspace()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var service = new RuntimeLogService(Path.Combine(testRoot, "data"));
            service.WriteInformation("startup-only");
            Assert.True(service.TryUseWorkspace(Path.Combine(testRoot, "workspace-a")));
            service.WriteInformation("workspace-a-only");
            var firstWorkspaceLog = service.CurrentLogPath;

            Assert.True(service.TryUseWorkspace(Path.Combine(testRoot, "workspace-b")));
            service.WriteInformation("workspace-b-only");

            var secondWorkspaceContent = service.ReadRecent();
            Assert.DoesNotContain("startup-only", secondWorkspaceContent);
            Assert.DoesNotContain("workspace-a-only", secondWorkspaceContent);
            Assert.Contains("workspace-b-only", secondWorkspaceContent);
            Assert.Contains("workspace-a-only", File.ReadAllText(firstWorkspaceLog));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void ReadLogFileRejectsSymbolicLinksOutsideTheLogDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = CreateTestRoot();
        try
        {
            var service = new RuntimeLogService(Path.Combine(testRoot, "data"));
            var outsideLog = Path.Combine(testRoot, "outside.log");
            File.WriteAllText(outsideLog, "private content");
            var linkedLog = Path.Combine(service.LogDirectory, "linked.log");
            File.CreateSymbolicLink(linkedLog, outsideLog);

            Assert.Throws<InvalidOperationException>(() =>
                service.ReadLogFile(linkedLog));
            Assert.DoesNotContain(linkedLog, service.GetLogFiles());
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
