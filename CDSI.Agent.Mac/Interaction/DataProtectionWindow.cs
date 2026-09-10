using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CDSI.Agent.Infrastructure.Persistence;
using CDSI.Agent.Mac.Platform;
using CDSI.Agent.Mac.Services;

namespace CDSI.Agent.Mac.Interaction;

/// <summary>
/// Creates, validates, exports, and schedules restoration of two-database state bundles.
/// The caller must close and restart the application when <see cref="RestartRequested"/>
/// becomes true.
/// </summary>
public sealed class DataProtectionWindow : Window
{
    private static readonly FilePickerFileType StateBackupFiles = new(
        "Beacon 状态备份")
    {
        Patterns = ["*.cdsibak"]
    };

    private readonly AppServices _services;
    private readonly ObservableCollection<StateBackupRow> _rows = [];
    private readonly DataGrid _backupGrid = new();
    private readonly TextBlock _summaryText = new();
    private readonly TextBlock _directoryText = new();
    private readonly TextBlock _statusText = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _createButton;
    private readonly Button _restoreFileButton;
    private readonly Button _validateButton;
    private readonly Button _exportButton;
    private readonly Button _openDirectoryButton;
    private readonly Button _refreshButton;
    private readonly Button _restoreSelectedButton;
    private readonly Button _closeButton;
    private string? _workspacePath;
    private bool _initialized;
    private bool _busy;

    public DataProtectionWindow(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Title = "数据保护";
        Width = 980;
        Height = 650;
        MinWidth = 760;
        MinHeight = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Avalonia.Media.Brushes.White;

        _createButton = CreateButton("立即创建状态备份", CreateBackupAsync);
        _restoreFileButton = CreateButton("从文件恢复...", RestoreFromFileAsync);
        _validateButton = CreateButton("验证所选", ValidateSelectedAsync);
        _exportButton = CreateButton("导出副本...", ExportSelectedAsync);
        _openDirectoryButton = CreateButton("打开备份目录", OpenBackupDirectoryAsync);
        _refreshButton = CreateButton("刷新", ReloadBackupsAsync);
        _restoreSelectedButton = CreateButton("恢复所选备份...", RestoreSelectedAsync);
        _closeButton = CreateButton("关闭", () =>
        {
            Close();
            return Task.CompletedTask;
        });

        ConfigureBackupGrid();
        Content = BuildContent();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    public bool RestartRequested { get; private set; }

    public StateRestorePreparation? RestorePreparation { get; private set; }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await InitializeAsync();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_busy && !RestartRequested)
        {
            e.Cancel = true;
        }
    }

    private async Task InitializeAsync()
    {
        SetBusy(true, "正在读取 CDSI 工作目录");
        try
        {
            _workspacePath = (await _services.Workspace.GetAsync())?.Path;
            if (string.IsNullOrWhiteSpace(_workspacePath))
            {
                await UiDialogs.ShowMessageAsync(
                    this,
                    "无法打开数据保护",
                    "尚未配置 CDSI 工作目录。请先在设置中配置工作目录。");
                SetBusy(false);
                Close();
                return;
            }

            await ReloadBackupsCoreAsync();
            _statusText.Text = "状态备份列表已刷新";
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("无法读取状态备份", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Control BuildContent()
    {
        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        AddToolbarButton(toolbar, _createButton);
        AddToolbarButton(toolbar, _restoreFileButton);
        AddToolbarButton(toolbar, _validateButton);
        AddToolbarButton(toolbar, _exportButton);
        AddToolbarButton(toolbar, _openDirectoryButton);
        AddToolbarButton(toolbar, _refreshButton);

        _summaryText.FontWeight = Avalonia.Media.FontWeight.SemiBold;
        _summaryText.Text = "正在读取状态备份";
        _directoryText.Foreground = Avalonia.Media.Brushes.DimGray;
        _directoryText.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _statusText.Foreground = Avalonia.Media.Brushes.DimGray;
        _statusText.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        _progress.IsIndeterminate = true;
        _progress.Width = 180;
        _progress.Height = 5;
        _progress.IsVisible = false;

        var header = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock
                {
                    Text = "Beacon 状态备份",
                    FontSize = 20,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "每个状态包同时包含资产数据库和 Reader 数据库。凭据、SSH 私钥、本地素材与云端对象不包含在内。",
                    Foreground = Avalonia.Media.Brushes.DimGray,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                _summaryText,
                _directoryText
            }
        };

        var operationRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12
        };
        operationRow.Children.Add(_statusText);
        Grid.SetColumn(_progress, 1);
        operationRow.Children.Add(_progress);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { _restoreSelectedButton, _closeButton }
        };

        var layout = new Grid
        {
            Margin = new Thickness(22),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            RowSpacing = 12
        };
        AddAtRow(layout, header, 0);
        AddAtRow(layout, toolbar, 1);
        AddAtRow(layout, _backupGrid, 2);
        AddAtRow(layout, operationRow, 3);
        AddAtRow(layout, footer, 4);
        return layout;
    }

    private void ConfigureBackupGrid()
    {
        _backupGrid.ItemsSource = _rows;
        _backupGrid.AutoGenerateColumns = false;
        _backupGrid.IsReadOnly = true;
        _backupGrid.SelectionMode = DataGridSelectionMode.Single;
        _backupGrid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _backupGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _backupGrid.Columns.Add(CreateTextColumn("创建时间", nameof(StateBackupRow.CreatedAt), 150));
        _backupGrid.Columns.Add(CreateTextColumn("类型", nameof(StateBackupRow.Kind), 120));
        _backupGrid.Columns.Add(CreateTextColumn("Beacon 版本", nameof(StateBackupRow.Version), 105));
        _backupGrid.Columns.Add(CreateTextColumn("内容", nameof(StateBackupRow.Content), 95));
        _backupGrid.Columns.Add(CreateTextColumn("大小", nameof(StateBackupRow.Size), 100));
        _backupGrid.Columns.Add(CreateTextColumn("状态", nameof(StateBackupRow.Status), 90));
        _backupGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "位置",
            Binding = new Binding(nameof(StateBackupRow.Path)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 260
        });
        _backupGrid.SelectionChanged += (_, _) => UpdateActionState();
        _backupGrid.DoubleTapped += async (_, e) =>
        {
            if (e.Source is Control)
            {
                await RestoreSelectedAsync();
            }
        };
    }

    private async Task ReloadBackupsAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_workspacePath))
        {
            return;
        }

        SetBusy(true, "正在验证本地状态备份");
        try
        {
            await ReloadBackupsCoreAsync();
            _statusText.Text = "状态备份列表已刷新";
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("无法读取状态备份", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReloadBackupsCoreAsync(string? pathToSelect = null)
    {
        var workspacePath = RequireWorkspacePath();
        pathToSelect ??= SelectedBackup?.Path;
        var backups = await _services.StateProtection.ListBackupsAsync(workspacePath);
        _rows.Clear();
        foreach (var backup in backups)
        {
            _rows.Add(new StateBackupRow(backup));
        }

        _backupGrid.SelectedItem = _rows.FirstOrDefault(row => PathsEqual(
                row.Path,
                pathToSelect)) ??
            _rows.FirstOrDefault();
        var restorable = backups.Count(backup =>
            backup.Status == LocalStateBackupStatus.Restorable);
        var latest = backups
            .Where(backup => backup.Status == LocalStateBackupStatus.Restorable)
            .MaxBy(backup => backup.CreatedAtUtc)?.CreatedAtUtc;
        _summaryText.Text = latest is null
            ? "尚无可恢复的状态备份"
            : $"可恢复备份 {restorable:N0} 份 · 最近备份 {latest.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _directoryText.Text = $"备份位置：{_services.StateProtection.GetBackupDirectory(workspacePath)}";
        UpdateActionState();
    }

    private async Task CreateBackupAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "正在创建状态备份");
        try
        {
            var backup = await _services.StateProtection.CreateBackupAsync(
                RequireWorkspacePath(),
                LocalStateBackupKind.Manual,
                _services.ClientIdentity.Value);
            await ReloadBackupsCoreAsync(backup.Path);
            _statusText.Text = $"状态备份已创建：{Path.GetFileName(backup.Path)}";
            TryLog($"已创建 Beacon 状态备份：{backup.Path}");
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("无法创建状态备份", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ValidateSelectedAsync()
    {
        var selected = SelectedBackup;
        if (_busy || selected is null)
        {
            return;
        }

        SetBusy(true, "正在验证所选状态备份");
        try
        {
            var inspected = await _services.StateProtection.InspectAsync(selected.Path);
            var rowIndex = _rows.ToList().FindIndex(row => PathsEqual(row.Path, selected.Path));
            if (rowIndex >= 0)
            {
                var replacement = new StateBackupRow(inspected);
                _rows[rowIndex] = replacement;
                _backupGrid.SelectedItem = replacement;
            }

            _statusText.Text = inspected.Status == LocalStateBackupStatus.Restorable
                ? "所选状态备份验证通过"
                : inspected.Error ?? "所选状态备份验证失败";
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("无法验证状态备份", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExportSelectedAsync()
    {
        var selected = SelectedBackup;
        if (_busy || selected?.Status != LocalStateBackupStatus.Restorable)
        {
            await UiDialogs.ShowMessageAsync(
                this,
                "导出状态备份",
                "请先选择一个状态为“可恢复”的备份。");
            return;
        }

        var destination = await UiDialogs.ChooseSaveFileAsync(
            this,
            "导出 Beacon 状态备份副本",
            CreateExportFilename(selected.CreatedAtUtc ?? DateTimeOffset.UtcNow),
            StateBackupFiles);
        if (destination is null)
        {
            return;
        }

        SetBusy(true, "正在验证并导出状态备份副本");
        try
        {
            var exported = await _services.StateProtection.ExportAsync(
                selected.Path,
                destination,
                selected,
                overwrite: true);
            _statusText.Text = $"状态备份副本已导出：{exported}";
            TryLog($"已导出 Beacon 状态备份副本：{exported}");
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("无法导出状态备份副本", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RestoreFromFileAsync()
    {
        if (_busy)
        {
            return;
        }

        var path = await UiDialogs.ChooseOpenFileAsync(
            this,
            "选择 Beacon 状态备份",
            StateBackupFiles);
        if (path is not null)
        {
            await InspectAndPrepareRestoreAsync(path);
        }
    }

    private async Task RestoreSelectedAsync()
    {
        var selected = SelectedBackup;
        if (_busy || selected?.Status != LocalStateBackupStatus.Restorable)
        {
            if (!_busy)
            {
                await UiDialogs.ShowMessageAsync(
                    this,
                    "恢复状态备份",
                    "请先选择一个状态为“可恢复”的备份。");
            }

            return;
        }

        await InspectAndPrepareRestoreAsync(selected.Path, selected);
    }

    private async Task InspectAndPrepareRestoreAsync(
        string path,
        LocalStateBackupInfo? expected = null)
    {
        SetBusy(true, "正在验证所选状态备份");
        LocalStateBackupInfo backup;
        try
        {
            backup = await _services.StateProtection.InspectAsync(path);
            if (expected is not null &&
                (!string.Equals(
                    expected.BundleSha256,
                    backup.BundleSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                 expected.BackupId != backup.BackupId))
            {
                throw new IOException("状态备份在选择后发生变化，请重新选择并验证。");
            }
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("无法验证状态备份", exception);
            SetBusy(false);
            return;
        }

        SetBusy(false);
        if (backup.Status != LocalStateBackupStatus.Restorable)
        {
            var message = backup.Status == LocalStateBackupStatus.NewerVersion
                ? "此备份由更高版本的 Beacon 创建。请先升级 Beacon，再执行恢复。"
                : backup.Error ?? "备份校验失败，文件可能已损坏或被修改。当前数据未更改。";
            await UiDialogs.ShowMessageAsync(this, "无法恢复状态备份", message);
            return;
        }

        if (!await UiDialogs.ConfirmAsync(
                this,
                "恢复 Beacon 状态",
                CreateRestoreConfirmation(backup),
                "安排恢复",
                destructive: true))
        {
            return;
        }

        SetBusy(true, "正在创建恢复前安全备份并安排恢复");
        try
        {
            RestorePreparation = await _services.StateProtection.PrepareRestoreAsync(
                backup.Path,
                RequireWorkspacePath(),
                _services.ClientIdentity.Value,
                backup);
            TryLog(
                $"已安排 Beacon 状态恢复；RestoreId={RestorePreparation.RestoreId:D}；" +
                $"状态备份={backup.Path}；安全备份={RestorePreparation.SafetyBackupPath}");
            RestartRequested = true;
            Close();
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("无法安排状态恢复", exception);
            SetBusy(false);
        }
    }

    private Task OpenBackupDirectoryAsync()
    {
        try
        {
            var path = _services.StateProtection.GetBackupDirectory(
                RequireWorkspacePath());
            Directory.CreateDirectory(path);
            MacPlatformIntegration.OpenPath(path);
        }
        catch (Exception exception)
        {
            return ShowErrorAsync("无法打开状态备份目录", exception);
        }

        return Task.CompletedTask;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            _statusText.Text = status;
        }

        _progress.IsVisible = busy;
        _backupGrid.IsEnabled = !busy;
        _createButton.IsEnabled = !busy && _workspacePath is not null;
        _restoreFileButton.IsEnabled = !busy && _workspacePath is not null;
        _openDirectoryButton.IsEnabled = !busy && _workspacePath is not null;
        _refreshButton.IsEnabled = !busy && _workspacePath is not null;
        _closeButton.IsEnabled = !busy;
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        var selected = SelectedBackup;
        _validateButton.IsEnabled = !_busy && selected is not null;
        _exportButton.IsEnabled = !_busy &&
            selected?.Status == LocalStateBackupStatus.Restorable;
        _restoreSelectedButton.IsEnabled = !_busy &&
            selected?.Status == LocalStateBackupStatus.Restorable;
    }

    private LocalStateBackupInfo? SelectedBackup =>
        (_backupGrid.SelectedItem as StateBackupRow)?.Source;

    private string RequireWorkspacePath()
    {
        return string.IsNullOrWhiteSpace(_workspacePath)
            ? throw new InvalidOperationException("尚未配置 CDSI 工作目录。")
            : _workspacePath;
    }

    private async Task ShowErrorAsync(string title, Exception exception)
    {
        TryLog(title, exception);
        _statusText.Text = $"{title}：{exception.Message}";
        await UiDialogs.ShowMessageAsync(this, title, exception.Message);
    }

    private void TryLog(string message, Exception? exception = null)
    {
        try
        {
            if (exception is null)
            {
                _services.RuntimeLog.WriteInformation(message);
            }
            else
            {
                _services.RuntimeLog.WriteError(message, exception);
            }
        }
        catch
        {
            // A logging failure must not change the state-protection result.
        }
    }

    private static Button CreateButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 32,
            Padding = new Thickness(12, 4)
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static void AddToolbarButton(Panel panel, Button button)
    {
        button.Margin = new Thickness(0, 0, 8, 6);
        panel.Children.Add(button);
    }

    private static void AddAtRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private static DataGridTextColumn CreateTextColumn(
        string header,
        string property,
        double width)
    {
        return new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property),
            Width = new DataGridLength(width)
        };
    }

    private static string CreateExportFilename(DateTimeOffset createdAt) =>
        $"beacon-state-{createdAt.UtcDateTime:yyyyMMdd-HHmmss'Z'}.cdsibak";

    private static string CreateRestoreConfirmation(LocalStateBackupInfo backup)
    {
        var createdAt = backup.CreatedAtUtc?.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm:ss") ?? "未知时间";
        return
            $"将恢复 {createdAt} 创建的 Beacon 状态备份。\n\n" +
            "将恢复资产索引、项目、标签、发布及备份记录，以及 RSS 订阅、条目、已读和收藏状态。\n\n" +
            "不会恢复或修改本地素材、云端对象、macOS 钥匙串凭据、SSH 私钥、client-identity.json 或当前客户端 ID。\n\n" +
            "状态包内的绝对路径、RSS URL/内容、账号与连接元数据可能敏感，请作为私密数据保存。\n\n" +
            "当前状态会先创建恢复前安全备份。Beacon 随后必须关闭，并在重新启动时完成恢复。";
    }

    private static bool PathsEqual(string left, string? right)
    {
        return right is not null && string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private sealed record StateBackupRow(LocalStateBackupInfo Source)
    {
        public string CreatedAt => Source.CreatedAtUtc?.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm:ss") ?? "-";

        public string Kind => Source.Kind switch
        {
            LocalStateBackupKind.Manual => "手动备份",
            LocalStateBackupKind.PreRestore => "恢复前安全备份",
            _ => "未知"
        };

        public string Version => Source.BeaconVersion ?? "-";

        public string Content => "资产 + RSS";

        public string Size => FormatFileSize(Source.FileSize);

        public string Status => Source.Status switch
        {
            LocalStateBackupStatus.Restorable => "可恢复",
            LocalStateBackupStatus.NewerVersion => "版本过新",
            _ => "已损坏"
        };

        public string Path => Source.Path;

        private static string FormatFileSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            var value = (double)Math.Max(0, bytes);
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unit]}";
        }
    }
}
