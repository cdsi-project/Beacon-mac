using CDSI.Agent.Mac.Settings;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Settings;

public sealed class SettingsDialogAsyncOperationTests
{
    [Fact]
    public async Task RunAsyncKeepsCloseGuardActiveAndRejectsConcurrentRuns()
    {
        var operation = new SettingsDialogAsyncOperation();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stateChanges = new List<bool>();
        var concurrentRunStarted = false;

        var firstRun = operation.RunAsync(
            () => release.Task,
            _ => Task.CompletedTask,
            stateChanges.Add);

        Assert.True(operation.IsRunning);
        await operation.RunAsync(
            () =>
            {
                concurrentRunStarted = true;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            stateChanges.Add);

        Assert.False(concurrentRunStarted);
        release.SetResult(true);
        await firstRun;
        Assert.False(operation.IsRunning);
        Assert.Equal([true, false], stateChanges);
    }

    [Fact]
    public async Task RunAsyncContainsOperationReportingAndStateFailures()
    {
        var operation = new SettingsDialogAsyncOperation();
        var failureWasReported = false;
        var stateChangeAttempts = 0;

        await operation.RunAsync(
            () => Task.FromException(new InvalidOperationException("operation failed")),
            _ =>
            {
                failureWasReported = true;
                return Task.FromException(new InvalidOperationException("reporting failed"));
            },
            _ =>
            {
                stateChangeAttempts++;
                throw new InvalidOperationException("state update failed");
            });

        Assert.True(failureWasReported);
        Assert.Equal(2, stateChangeAttempts);
        Assert.False(operation.IsRunning);
    }

    [Fact]
    public async Task RunAsyncKeepsCloseGuardActiveUntilFailureReportingCompletes()
    {
        var operation = new SettingsDialogAsyncOperation();
        var releaseFailureReport = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failureReportStarted = false;

        var run = operation.RunAsync(
            () => Task.FromException(new InvalidOperationException("operation failed")),
            _ =>
            {
                failureReportStarted = true;
                return releaseFailureReport.Task;
            },
            _ => { });

        Assert.True(failureReportStarted);
        Assert.True(operation.IsRunning);
        releaseFailureReport.SetResult(true);
        await run;
        Assert.False(operation.IsRunning);
    }
}
