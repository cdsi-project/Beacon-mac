using System.Diagnostics;
using Avalonia;
using CDSI.Agent.Mac.Platform;
using CDSI.Agent.Mac.Services;

namespace CDSI.Agent.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (TryRunPendingRestoreRestartHelper(args))
        {
            return;
        }

        var dataDirectory = AppServices.GetDefaultDataDirectory();
        RuntimeLogService? runtimeLog = null;
        try
        {
            using var singleInstance =
                MacPlatformIntegration.CreateSingleInstanceCoordinator();
            if (!singleInstance.IsPrimaryInstance)
            {
                singleInstance.SignalPrimaryInstance();
                return;
            }

            runtimeLog = new RuntimeLogService(dataDirectory);
            runtimeLog.WriteInformation(
                $"CDSI Beacon v{AppServices.GetApplicationVersion()} 启动；" +
                $"OS={Environment.OSVersion}；Runtime={Environment.Version}");
            App.StartupRuntimeLog = runtimeLog;
            App.StartupState = ApplicationStartupCoordinator.PrepareAsync(
                    dataDirectory,
                    runtimeLog)
                .GetAwaiter()
                .GetResult();
            App.SingleInstance = singleInstance;
            App.RestartForPendingStateRestore = false;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            if (App.RestartForPendingStateRestore)
            {
                StartPendingRestoreRestartHelper(runtimeLog);
            }
        }
        catch (Exception exception)
        {
            runtimeLog?.WriteError("应用发生未处理异常", exception);
            StartupFailureReporter.Show(dataDirectory, exception, runtimeLog);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    internal static bool TryParsePendingRestoreRestartHelper(
        IReadOnlyList<string> args,
        out int parentProcessId)
    {
        parentProcessId = 0;
        return args.Count == 2 &&
            string.Equals(
                args[0],
                "--restart-for-pending-state-restore",
                StringComparison.Ordinal) &&
            int.TryParse(args[1], out parentProcessId) &&
            parentProcessId > 0 &&
            parentProcessId != Environment.ProcessId;
    }

    internal static ProcessStartInfo CreatePendingRestoreRestartHelperStartInfo(
        string executablePath,
        int parentProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (parentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--restart-for-pending-state-restore");
        startInfo.ArgumentList.Add(parentProcessId.ToString());
        return startInfo;
    }

    private static bool TryRunPendingRestoreRestartHelper(string[] args)
    {
        if (!TryParsePendingRestoreRestartHelper(args, out var parentProcessId))
        {
            return false;
        }

        try
        {
            try
            {
                using var parent = Process.GetProcessById(parentProcessId);
                parent.WaitForExit();
            }
            catch (ArgumentException)
            {
                // The parent exited before the helper acquired its process handle.
            }

            StartApplication(Environment.ProcessPath ??
                throw new InvalidOperationException("无法确定 Beacon 可执行文件路径。"));
        }
        catch (Exception exception)
        {
            var dataDirectory = AppServices.GetDefaultDataDirectory();
            StartupFailureReporter.Show(
                dataDirectory,
                new InvalidOperationException(
                    "Beacon 无法自动重新启动。待恢复状态仍已保留，请手动重新打开 Beacon。",
                    exception));
        }

        return true;
    }

    private static void StartPendingRestoreRestartHelper(RuntimeLogService runtimeLog)
    {
        try
        {
            var executablePath = Environment.ProcessPath ??
                throw new InvalidOperationException("无法确定 Beacon 可执行文件路径。");
            using var helper = Process.Start(
                CreatePendingRestoreRestartHelperStartInfo(
                    executablePath,
                    Environment.ProcessId)) ??
                throw new InvalidOperationException("无法启动状态恢复重启助手。");
        }
        catch (Exception exception)
        {
            runtimeLog.WriteError("无法自动重新启动 Beacon", exception);
            StartupFailureReporter.Show(
                AppServices.GetDefaultDataDirectory(),
                new InvalidOperationException(
                    "状态恢复已经安排，但 Beacon 无法自动重新启动。请手动重新打开 Beacon，恢复将在启动时继续。",
                    exception),
                runtimeLog);
        }
    }

    private static void StartApplication(string executablePath)
    {
        var appBundle = TryGetContainingAppBundle(executablePath);
        ProcessStartInfo startInfo;
        if (appBundle is not null && File.Exists("/usr/bin/open"))
        {
            startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(appBundle);
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(executablePath),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("无法启动 Beacon 进程。");
    }

    internal static string? TryGetContainingAppBundle(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var executableDirectory = Directory.GetParent(Path.GetFullPath(executablePath));
        var contentsDirectory = executableDirectory?.Parent;
        var appDirectory = contentsDirectory?.Parent;
        return executableDirectory?.Name == "MacOS" &&
            contentsDirectory?.Name == "Contents" &&
            appDirectory?.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true
                ? appDirectory.FullName
                : null;
    }
}
