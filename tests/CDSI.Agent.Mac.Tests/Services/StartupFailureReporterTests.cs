using CDSI.Agent.Infrastructure.Persistence;
using CDSI.Agent.Mac.Services;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Services;

public sealed class StartupFailureReporterTests
{
    [Fact]
    public void WriteFailureAndCreateMessage_ReportsTheActualWorkspaceLogFile()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var dataDirectory = Path.Combine(testRoot, "data");
            var workspace = Path.Combine(testRoot, "workspace");
            var runtimeLog = new RuntimeLogService(dataDirectory);
            Assert.True(runtimeLog.TryUseWorkspace(workspace));

            var message = StartupFailureReporter.WriteFailureAndCreateMessage(
                dataDirectory,
                new InvalidOperationException("startup failed"),
                runtimeLog);

            Assert.Contains("诊断日志：", message);
            Assert.Contains(runtimeLog.CurrentLogPath, message);
            Assert.DoesNotContain(Path.Combine(dataDirectory, "Logs"), message);
            Assert.Contains("startup failed", File.ReadAllText(runtimeLog.CurrentLogPath));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateUserFacingMessage_ReportsStateRestoreSafetyBackupPath()
    {
        var safetyBackupPath = Path.Combine(
            Path.GetTempPath(),
            "Beacon Safety Backups",
            Guid.NewGuid().ToString("N"));
        var exception = new StateRestoreFailedException(
            "状态恢复及自动回滚均失败。",
            currentStateIsSafe: false,
            new IOException("rollback failed"),
            safetyBackupPath);

        var message = StartupFailureReporter.CreateUserFacingMessage(
            exception,
            diagnosticLogPath: null);

        Assert.Contains("恢复前安全副本位置：", message);
        Assert.Contains(Path.GetFullPath(safetyBackupPath), message);
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
