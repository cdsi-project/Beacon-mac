namespace CDSI.Agent.Mac.Settings;

internal sealed class SettingsDialogAsyncOperation
{
    public bool IsRunning { get; private set; }

    public async Task RunAsync(
        Func<Task> operation,
        Func<Exception, Task> reportFailureAsync,
        Action<bool> updateState)
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        TryUpdateState(updateState, true);
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            try
            {
                await reportFailureAsync(exception);
            }
            catch
            {
                // Event-backed operations must never leak a second reporting failure.
            }
        }
        finally
        {
            IsRunning = false;
            TryUpdateState(updateState, false);
        }
    }

    private static void TryUpdateState(Action<bool> updateState, bool isRunning)
    {
        try
        {
            updateState(isRunning);
        }
        catch
        {
            // The async event boundary must remain non-throwing during teardown.
        }
    }
}
