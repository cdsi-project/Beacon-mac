using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace CDSI.Agent.Mac.Platform;

public static class MacPlatformIntegration
{
    public const string DefaultApplicationId = "com.cdsi.beacon";

    public static MacSingleInstanceCoordinator CreateSingleInstanceCoordinator(
        string applicationId = DefaultApplicationId) =>
        new(applicationId);

    public static void RevealInFinder(string path)
    {
        EnsureMacOS();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The item to reveal in Finder does not exist.",
                fullPath);
        }

        StartAndDispose(CreateRevealInFinderStartInfo(fullPath));
    }

    public static void OpenUrl(string url)
    {
        StartAndDispose(CreateOpenUrlStartInfo(url));
    }

    public static void OpenUrl(Uri url)
    {
        StartAndDispose(CreateOpenUrlStartInfo(url));
    }

    public static void OpenPath(string path)
    {
        EnsureMacOS();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The item to open does not exist.", fullPath);
        }

        var startInfo = CreateOpenStartInfo();
        startInfo.ArgumentList.Add(fullPath);
        StartAndDispose(startInfo);
    }

    public static ProcessStartInfo CreateRevealInFinderStartInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var startInfo = CreateOpenStartInfo();
        startInfo.ArgumentList.Add("-R");
        startInfo.ArgumentList.Add(Path.GetFullPath(path));
        return startInfo;
    }

    public static ProcessStartInfo CreateOpenUrlStartInfo(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("URL must be absolute.", nameof(url));
        }

        return CreateOpenUrlStartInfo(uri);
    }

    public static ProcessStartInfo CreateOpenUrlStartInfo(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "Only absolute HTTP and HTTPS URLs can be opened.",
                nameof(url));
        }

        var startInfo = CreateOpenStartInfo();
        startInfo.ArgumentList.Add(url.AbsoluteUri);
        return startInfo;
    }

    private static ProcessStartInfo CreateOpenStartInfo() => new()
    {
        FileName = "/usr/bin/open",
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static void StartAndDispose(ProcessStartInfo startInfo)
    {
        EnsureMacOS();
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "macOS did not start the requested application.");
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "This integration is only available on macOS.");
        }
    }
}

public sealed class MacSingleInstanceCoordinator : IDisposable
{
    private const int ActivationConnectionTimeoutMilliseconds = 2_000;

    private readonly Mutex _instanceMutex;
    private readonly bool _ownsInstanceMutex;
    private readonly string _activationPipeName;
    private readonly NamedPipeServerStream? _activationServer;
    private CancellationTokenSource? _listenerCancellation;
    private Task? _listenerTask;
    private int _listening;
    private int _disposed;

    public MacSingleInstanceCoordinator(string applicationId)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "macOS single-instance coordination is only available on macOS.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var scope = CreateInstanceScope(applicationId, Environment.UserName);
        _activationPipeName = CreateActivationPipeName(applicationId, Environment.UserName);

        Mutex? instanceMutex = null;
        var ownsInstanceMutex = false;
        try
        {
            instanceMutex = new Mutex(
                initiallyOwned: true,
                $"{scope}.mutex",
                out ownsInstanceMutex);
            _instanceMutex = instanceMutex;
            _ownsInstanceMutex = ownsInstanceMutex;
            IsPrimaryInstance = ownsInstanceMutex;
            if (IsPrimaryInstance)
            {
                _activationServer = CreateActivationServer(_activationPipeName);
            }
        }
        catch
        {
            if (ownsInstanceMutex)
            {
                instanceMutex?.ReleaseMutex();
            }

            instanceMutex?.Dispose();
            throw;
        }
    }

    public bool IsPrimaryInstance { get; }

    public void SignalPrimaryInstance()
    {
        ThrowIfDisposed();
        if (IsPrimaryInstance)
        {
            throw new InvalidOperationException(
                "The primary instance cannot send an activation request to itself.");
        }

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                _activationPipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect(ActivationConnectionTimeoutMilliseconds);
            client.WriteByte(1);
            client.Flush();
        }
        catch (Exception exception)
            when (exception is IOException or TimeoutException)
        {
            throw new InvalidOperationException(
                "Unable to signal the primary Beacon instance.",
                exception);
        }
    }

    public void StartListening(Action activationHandler)
    {
        ArgumentNullException.ThrowIfNull(activationHandler);
        ThrowIfDisposed();
        if (!IsPrimaryInstance)
        {
            throw new InvalidOperationException(
                "Only the primary instance can listen for activation requests.");
        }

        if (Interlocked.Exchange(ref _listening, 1) != 0)
        {
            throw new InvalidOperationException(
                "The activation listener has already been started.");
        }

        var cancellation = new CancellationTokenSource();
        _listenerCancellation = cancellation;
        var synchronizationContext = SynchronizationContext.Current;
        _listenerTask = ListenForActivationAsync(
            activationHandler,
            synchronizationContext,
            cancellation.Token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _listenerCancellation?.Cancel();
        _activationServer?.Dispose();
        _listenerCancellation?.Dispose();
        if (_ownsInstanceMutex)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Mutex ownership is thread-affine; disposing the handle still
                // releases it when shutdown occurs on a different thread.
            }
        }

        _instanceMutex.Dispose();
    }

    internal static string CreateInstanceScope(
        string applicationId,
        string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var input = Encoding.UTF8.GetBytes($"{applicationId}\n{userName}");
        try
        {
            var hash = SHA256.HashData(input);
            return $"cdsi-beacon-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    internal static string CreateActivationPipeName(
        string applicationId,
        string userName)
    {
        var scope = CreateInstanceScope(applicationId, userName);
        return $"c{scope[^24..]}";
    }

    private static NamedPipeServerStream CreateActivationServer(string pipeName) =>
        new(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenForActivationAsync(
        Action activationHandler,
        SynchronizationContext? synchronizationContext,
        CancellationToken cancellationToken)
    {
        var server = _activationServer!;
        var signal = new byte[1];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                var bytesRead = await server.ReadAsync(signal, cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead > 0 && Volatile.Read(ref _disposed) == 0)
                {
                    DispatchActivation(
                        activationHandler,
                        synchronizationContext);
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
                when (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            catch (IOException exception)
            {
                Trace.TraceWarning(
                    "Beacon activation listener failed: {0}",
                    exception.Message);
            }
            finally
            {
                if (server.IsConnected)
                {
                    server.Disconnect();
                }
            }
        }
    }

    private static void DispatchActivation(
        Action activationHandler,
        SynchronizationContext? synchronizationContext)
    {
        if (synchronizationContext is not null)
        {
            synchronizationContext.Post(
                static state => InvokeActivationHandler((Action)state!),
                activationHandler);
            return;
        }

        ThreadPool.QueueUserWorkItem(
            static state => InvokeActivationHandler((Action)state!),
            activationHandler);
    }

    private static void InvokeActivationHandler(Action activationHandler)
    {
        try
        {
            activationHandler();
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "Beacon activation handler failed: {0}",
                exception);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}
