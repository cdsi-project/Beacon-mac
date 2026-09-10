using System.Diagnostics;
using CDSI.Agent.Infrastructure.Persistence;

namespace CDSI.Agent.Mac.Services;

internal static class StartupFailureReporter
{
    public static void Show(
        string dataDirectory,
        Exception exception,
        RuntimeLogService? runtimeLog = null)
    {
        var message = WriteFailureAndCreateMessage(
            dataDirectory,
            exception,
            runtimeLog);
        Console.Error.WriteLine($"CDSI Beacon 无法启动: {message}");
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("on run argv");
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(
                "display alert \"CDSI Beacon 无法启动\" message (item 1 of argv) as critical buttons {\"退出\"} default button \"退出\"");
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add("end run");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(message);
            using var process = Process.Start(startInfo);
            process?.WaitForExit();
        }
        catch
        {
            // stderr is the final fallback for a pre-Avalonia startup failure.
        }
    }

    internal static string WriteFailureAndCreateMessage(
        string dataDirectory,
        Exception exception,
        RuntimeLogService? runtimeLog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(exception);

        string? diagnosticLogPath = null;
        try
        {
            runtimeLog ??= new RuntimeLogService(dataDirectory);
            diagnosticLogPath = runtimeLog.TryWriteError(
                "应用发生未处理的启动异常",
                exception);
        }
        catch
        {
            // The native alert and stderr remain available if logging cannot start.
        }

        return CreateUserFacingMessage(exception, diagnosticLogPath);
    }

    internal static string CreateUserFacingMessage(
        Exception exception,
        string? diagnosticLogPath)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var safetyBackupPath = exception is StateRestoreFailedException
        {
            SafetyBackupPath: { } path
        }
            ? NormalizeReportablePath(path)
            : null;
        var safetyBackupText = safetyBackupPath is null
            ? string.Empty
            : $"\n\n恢复前安全副本位置：\n{safetyBackupPath}";
        var reportableLogPath = NormalizeReportablePath(diagnosticLogPath);
        var diagnosticLogText = reportableLogPath is null
            ? string.Empty
            : $"\n\n诊断日志：\n{reportableLogPath}";
        return RuntimeLogService.RedactSensitiveText(
            $"{exception.Message}{safetyBackupText}{diagnosticLogText}");
    }

    private static string? NormalizeReportablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          NotSupportedException or
                                          PathTooLongException or
                                          System.Security.SecurityException)
        {
            return null;
        }
    }
}
