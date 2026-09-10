using System.Text;
using System.Text.RegularExpressions;

namespace CDSI.Agent.Mac.Services;

public sealed class RuntimeLogService
{
    private static readonly Regex SecretAssignmentPattern = new(
        @"\b((?:aws[_-]?)?secret[_-]?access[_-]?key|access[_-]?key[_-]?secret|api[_-]?key|client[_-]?secret|security[_-]?token|secret|password|passwd|token|signature)\b(\s*[:=]\s*)(""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex AuthorizationPattern = new(
        @"\b(authorization|proxy-authorization)\b(\s*[:=]\s*)(?:(?:bearer|basic)\s+)?[^\s,;]+",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex UrlUserInfoPattern = new(
        @"\b(https?://)[^/@\s]+@",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex UrlQueryPattern = new(
        @"(https?://[^\s?]+)\?[^\s]+",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private readonly object _sync = new();
    private string _logDirectory;
    private string _currentLogPath;
    private bool _hasWorkspaceLogDirectory;

    public RuntimeLogService(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _logDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "Logs");
        _currentLogPath = Path.Combine(
            _logDirectory,
            $"beacon-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.log");
        try
        {
            Directory.CreateDirectory(_logDirectory);
        }
        catch
        {
            // Startup failure reporting still has stderr and a native alert fallback.
        }
    }

    public string LogDirectory
    {
        get
        {
            lock (_sync)
            {
                return _logDirectory;
            }
        }
    }

    public string CurrentLogPath
    {
        get
        {
            lock (_sync)
            {
                return _currentLogPath;
            }
        }
    }

    public bool TryUseWorkspace(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        try
        {
            var targetDirectory = GetWorkspaceLogDirectory(workspacePath);
            lock (_sync)
            {
                if (PathsEqual(_logDirectory, targetDirectory))
                {
                    return true;
                }

                Directory.CreateDirectory(targetDirectory);
                var targetPath = CreateAvailableLogPath(
                    targetDirectory,
                    Path.GetFileName(_currentLogPath));
                if (!_hasWorkspaceLogDirectory && File.Exists(_currentLogPath))
                {
                    File.Copy(_currentLogPath, targetPath, overwrite: false);
                }

                _logDirectory = targetDirectory;
                _currentLogPath = targetPath;
                _hasWorkspaceLogDirectory = true;
            }

            WriteInformation($"运行日志目录已切换到工作目录：{targetDirectory}");
            return true;
        }
        catch (Exception exception)
        {
            WriteError("无法将运行日志目录切换到工作目录", exception);
            return false;
        }
    }

    public static string GetWorkspaceLogDirectory(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        return Path.Combine(Path.GetFullPath(workspacePath), "System", "Logs");
    }

    public void WriteInformation(string message) => Write("INFO", message, null);

    public void WriteError(string message, Exception exception) =>
        Write("ERROR", message, exception);

    internal string? TryWriteError(string message, Exception exception) =>
        Write("ERROR", message, exception);

    public string ReadRecent(int maximumCharacters = 100_000)
    {
        if (maximumCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        lock (_sync)
        {
            var path = _currentLogPath;
            if (!File.Exists(path))
            {
                return "当前还没有运行日志。";
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            return text.Length <= maximumCharacters
                ? text
                : text[^maximumCharacters..];
        }
    }

    public IReadOnlyList<string> GetLogFiles()
    {
        var directory = LogDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            return Directory
                .EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                .Where(path => IsSafeLogFile(directory, path))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public string ReadLogFile(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        var directory = LogDirectory;
        var fullPath = Path.GetFullPath(logPath);
        if (!IsSafeLogFile(directory, fullPath))
        {
            throw new InvalidOperationException("只能读取 Beacon 日志目录中的文件。");
        }

        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private string? Write(string level, string message, Exception? exception)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [")
                .Append(level)
                .Append("] ")
                .AppendLine(RedactSensitiveText(message.Trim()));
            if (exception is not null)
            {
                entry.AppendLine(RedactSensitiveText(exception.ToString()));
            }

            lock (_sync)
            {
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(_currentLogPath, entry.ToString(), Encoding.UTF8);
                return _currentLogPath;
            }
        }
        catch
        {
            // Diagnostics must never prevent startup, an operation, or shutdown.
            return null;
        }
    }

    internal static string RedactSensitiveText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = AuthorizationPattern.Replace(
            text,
            match => $"{match.Groups[1].Value}{match.Groups[2].Value}[REDACTED]");
        redacted = SecretAssignmentPattern.Replace(
            redacted,
            match => $"{match.Groups[1].Value}{match.Groups[2].Value}[REDACTED]");
        redacted = UrlUserInfoPattern.Replace(redacted, "$1[REDACTED]@");
        return UrlQueryPattern.Replace(redacted, "$1?[REDACTED]");
    }

    private static string CreateAvailableLogPath(
        string logDirectory,
        string filename)
    {
        var candidate = Path.Combine(logDirectory, filename);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var name = Path.GetFileNameWithoutExtension(filename);
        var extension = Path.GetExtension(filename);
        for (var suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(
                logDirectory,
                $"{name}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsSafeLogFile(string logDirectory, string logPath)
    {
        try
        {
            var fullDirectory = Path.GetFullPath(logDirectory);
            var fullPath = Path.GetFullPath(logPath);
            if (!PathsEqual(Path.GetDirectoryName(fullPath) ?? string.Empty, fullDirectory) ||
                !string.Equals(
                    Path.GetExtension(fullPath),
                    ".log",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var attributes = File.GetAttributes(fullPath);
            return (attributes & FileAttributes.ReparsePoint) == 0 &&
                new FileInfo(fullPath).LinkTarget is null;
        }
        catch
        {
            return false;
        }
    }
}
