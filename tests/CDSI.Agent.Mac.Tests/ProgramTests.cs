using Xunit;

namespace CDSI.Agent.Mac.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void PendingRestoreHelperArgumentsAreParsedStrictly()
    {
        var expectedProcessId = Environment.ProcessId + 1;

        var result = Program.TryParsePendingRestoreRestartHelper(
            ["--restart-for-pending-state-restore", expectedProcessId.ToString()],
            out var parsedProcessId);

        Assert.True(result);
        Assert.Equal(expectedProcessId, parsedProcessId);
    }

    [Theory]
    [InlineData("--restart-for-pending-state-restore", "0", 0)]
    [InlineData("--restart-for-pending-state-restore", "not-a-process", 0)]
    [InlineData("--other", "42", 0)]
    public void PendingRestoreHelperRejectsInvalidArguments(
        string option,
        string processId,
        int expectedProcessId)
    {
        var result = Program.TryParsePendingRestoreRestartHelper(
            [option, processId],
            out var parsedProcessId);

        Assert.False(result);
        Assert.Equal(expectedProcessId, parsedProcessId);
    }

    [Fact]
    public void PendingRestoreHelperRejectsUnexpectedArgumentCounts()
    {
        Assert.False(Program.TryParsePendingRestoreRestartHelper([], out _));
        Assert.False(Program.TryParsePendingRestoreRestartHelper(
            ["--restart-for-pending-state-restore", "42", "extra"],
            out _));
    }

    [Fact]
    public void PendingRestoreHelperStartInfoUsesStructuredArguments()
    {
        var executable = Path.Combine(Path.GetTempPath(), "Beacon App", "CDSI-Beacon");

        var startInfo = Program.CreatePendingRestoreRestartHelperStartInfo(executable, 42);

        Assert.Equal(Path.GetFullPath(executable), startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            ["--restart-for-pending-state-restore", "42"],
            startInfo.ArgumentList.ToArray());
    }

    [Theory]
    [InlineData("/Applications/CDSI Beacon.app/Contents/MacOS/CDSI-Beacon",
        "/Applications/CDSI Beacon.app")]
    [InlineData("/tmp/CDSI-Beacon", null)]
    [InlineData("/Applications/CDSI Beacon/Contents/MacOS/CDSI-Beacon", null)]
    public void ContainingAppBundleIsDetectedFromStandardLayout(
        string executablePath,
        string? expectedBundle)
    {
        Assert.Equal(expectedBundle, Program.TryGetContainingAppBundle(executablePath));
    }
}
