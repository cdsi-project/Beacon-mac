using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Mac.Interaction;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Services;

/// <summary>
/// Runs low-frequency maintenance that should only execute while the main UI is idle.
/// </summary>
public sealed class BackgroundServiceCoordinator : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _idleScanTimer;
    private readonly DispatcherTimer _databaseBackupTimer;
    private readonly DispatcherTimer _volumeDebounceTimer;
    private FileSystemWatcher? _volumeWatcher;
    private bool _started;
    private bool _idleScanCheckInProgress;
    private bool _databaseBackupInProgress;
    private bool _disposed;

    public BackgroundServiceCoordinator(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _idleScanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _idleScanTimer.Tick += OnIdleScanTimerTick;
        _databaseBackupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(1)
        };
        _databaseBackupTimer.Tick += OnDatabaseBackupTimerTick;
        _volumeDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _volumeDebounceTimer.Tick += OnVolumeDebounceTimerTick;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _idleScanTimer.Start();
        _databaseBackupTimer.Start();
        if (Directory.Exists("/Volumes"))
        {
            _volumeWatcher = new FileSystemWatcher("/Volumes")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            _volumeWatcher.Created += OnMountedVolumesChanged;
            _volumeWatcher.Deleted += OnMountedVolumesChanged;
            _volumeWatcher.Renamed += OnMountedVolumesChanged;
        }
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _idleScanTimer.Stop();
        _databaseBackupTimer.Stop();
        _volumeDebounceTimer.Stop();
        if (_volumeWatcher is not null)
        {
            _volumeWatcher.EnableRaisingEvents = false;
            _volumeWatcher.Dispose();
            _volumeWatcher = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _idleScanTimer.Tick -= OnIdleScanTimerTick;
        _databaseBackupTimer.Tick -= OnDatabaseBackupTimerTick;
        _volumeDebounceTimer.Tick -= OnVolumeDebounceTimerTick;
        _disposed = true;
    }

    internal static IReadOnlyList<ScanRoot> GetDueIdleScanRoots(
        IEnumerable<ScanRoot> roots,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return roots.Where(root =>
        {
            var anchor = root.LastScannedAt is { } scanned && scanned > root.UpdatedAt
                ? scanned
                : root.UpdatedAt;
            return root.Mode == ScanRootMode.Readonly &&
                root.Enabled &&
                root.Status != ScanRootStatus.Offline &&
                root.GetIdleScanSchedule().IsDue(anchor, now);
        }).ToArray();
    }

    internal static async Task RunIdleScanCheckAsync(
        Func<Task<IReadOnlyList<ScanRoot>>> listScanRootsAsync,
        Func<IReadOnlyCollection<Guid>, Task> runIdleScanAsync,
        Func<bool> shouldDefer,
        RuntimeLogService runtimeLog,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(listScanRootsAsync);
        ArgumentNullException.ThrowIfNull(runIdleScanAsync);
        ArgumentNullException.ThrowIfNull(shouldDefer);
        ArgumentNullException.ThrowIfNull(runtimeLog);

        try
        {
            var roots = await listScanRootsAsync();
            if (shouldDefer())
            {
                return;
            }

            var due = GetDueIdleScanRoots(roots, now);
            if (due.Count > 0)
            {
                await runIdleScanAsync(due.Select(root => root.Id).ToArray());
            }
        }
        catch (Exception exception)
        {
            runtimeLog.WriteError("检查空闲扫描计划失败", exception);
        }
    }

    private async void OnIdleScanTimerTick(object? sender, EventArgs e)
    {
        if (!_started || _disposed || _viewModel.IsBusy ||
            _idleScanCheckInProgress || HasBlockingSecondaryWindow())
        {
            return;
        }

        _idleScanCheckInProgress = true;
        try
        {
            await RunIdleScanCheckAsync(
                () => _viewModel.Services.Scan.ListScanRootsAsync(),
                scanRootIds => _viewModel.RunIdleScanAsync(scanRootIds),
                () => _viewModel.IsBusy || HasBlockingSecondaryWindow(),
                _viewModel.Services.RuntimeLog,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _idleScanCheckInProgress = false;
        }
    }

    private void OnMountedVolumesChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_started || _disposed)
            {
                return;
            }

            _volumeDebounceTimer.Stop();
            _volumeDebounceTimer.Start();
        });
    }

    private async void OnVolumeDebounceTimerTick(object? sender, EventArgs e)
    {
        _volumeDebounceTimer.Stop();
        if (!_started || _disposed)
        {
            return;
        }

        if (_viewModel.IsBusy || HasBlockingSecondaryWindow())
        {
            _volumeDebounceTimer.Start();
            return;
        }

        await _viewModel.ReconcileLocalVolumesAsync();
    }

    private async void OnDatabaseBackupTimerTick(object? sender, EventArgs e)
    {
        if (!_started || _disposed || _viewModel.IsBusy ||
            _databaseBackupInProgress || HasBlockingSecondaryWindow())
        {
            return;
        }

        _databaseBackupInProgress = true;
        try
        {
            await _viewModel.RunUiOperationAsync(
                "正在创建自动数据库快照",
                allowCancel: false,
                cancellationToken => _viewModel.CreateDatabaseSnapshotsAsync(
                    force: false,
                    cancellationToken));
        }
        catch (Exception exception)
        {
            _viewModel.Services.RuntimeLog.WriteError(
                "自动创建本地数据库快照失败",
                exception);
        }
        finally
        {
            _databaseBackupInProgress = false;
        }
    }

    private static bool HasBlockingSecondaryWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime is
                IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.Windows.Any(window =>
                window.IsVisible &&
                !ReferenceEquals(window, desktop.MainWindow) &&
                window is not TaskCenterWindow);
    }
}
