using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using CDSI.Agent.Mac.Platform;
using CDSI.Agent.Mac.Services;

namespace CDSI.Agent.Mac.Interaction;

public sealed class RuntimeLogWindow : Window
{
    private readonly RuntimeLogService _runtimeLog;
    private readonly ObservableCollection<LogFileItem> _logFiles = [];
    private readonly ComboBox _logFilePicker = new();
    private readonly TextBox _content = new();
    private readonly TextBlock _pathText = new();
    private readonly DispatcherTimer _refreshTimer;

    public RuntimeLogWindow(RuntimeLogService runtimeLog)
    {
        _runtimeLog = runtimeLog ?? throw new ArgumentNullException(nameof(runtimeLog));

        Title = "运行日志";
        Width = 920;
        Height = 620;
        MinWidth = 600;
        MinHeight = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _logFilePicker.ItemsSource = _logFiles;
        _logFilePicker.MinWidth = 280;
        _logFilePicker.SelectionChanged += (_, _) => RefreshContent();
        _pathText.Text = _runtimeLog.CurrentLogPath;
        _pathText.Foreground = Avalonia.Media.Brushes.DimGray;
        _pathText.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        _content.IsReadOnly = true;
        _content.AcceptsReturn = true;
        _content.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        ScrollViewer.SetVerticalScrollBarVisibility(
            _content,
            ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(
            _content,
            ScrollBarVisibility.Auto);

        var refresh = CreateButton("刷新", Refresh);
        var copy = CreateButton("复制全部", CopyAllAsync);
        var openDirectory = CreateButton("打开日志目录", OpenDirectoryAsync);
        var close = CreateButton("关闭", CloseWindow);
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "日志文件",
                    VerticalAlignment = VerticalAlignment.Center
                },
                _logFilePicker,
                refresh,
                copy,
                openDirectory
            }
        };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { close }
        };

        var layout = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 10
        };
        AddAtRow(layout, toolbar, 0);
        AddAtRow(layout, _pathText, 1);
        AddAtRow(layout, _content, 2);
        AddAtRow(layout, footer, 3);
        Content = layout;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += (_, _) => RefreshContent();
        Opened += (_, _) =>
        {
            Refresh();
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    public void Refresh()
    {
        var selectedPath = (_logFilePicker.SelectedItem as LogFileItem)?.Path;
        try
        {
            var paths = _runtimeLog.GetLogFiles();
            _logFiles.Clear();
            foreach (var path in paths)
            {
                _logFiles.Add(new LogFileItem(path));
            }

            _logFilePicker.SelectedItem = _logFiles.FirstOrDefault(item =>
                    PathsEqual(item.Path, selectedPath)) ??
                _logFiles.FirstOrDefault(item =>
                    PathsEqual(item.Path, _runtimeLog.CurrentLogPath)) ??
                _logFiles.FirstOrDefault();
            if (_logFilePicker.SelectedItem is null)
            {
                _pathText.Text = _runtimeLog.LogDirectory;
                _content.Text = "暂无运行日志。";
                return;
            }

            RefreshContent();
        }
        catch (Exception exception)
        {
            _content.Text = $"无法读取运行日志：{exception.Message}";
        }
    }

    private void RefreshContent()
    {
        if (_logFilePicker.SelectedItem is not LogFileItem selected)
        {
            return;
        }

        try
        {
            var wasAtEnd = _content.CaretIndex >= (_content.Text?.Length ?? 0) - 1;
            var previousCaret = _content.CaretIndex;
            var text = _runtimeLog.ReadLogFile(selected.Path);
            if (!string.Equals(_content.Text, text, StringComparison.Ordinal))
            {
                _content.Text = text;
                _content.CaretIndex = wasAtEnd
                    ? text.Length
                    : Math.Min(previousCaret, text.Length);
            }

            _pathText.Text = selected.Path;
        }
        catch (Exception exception)
        {
            _pathText.Text = selected.Path;
            _content.Text = $"无法读取运行日志：{exception.Message}";
        }
    }

    private async Task CopyAllAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(_content.Text ?? string.Empty);
        }
    }

    private async Task OpenDirectoryAsync()
    {
        try
        {
            Directory.CreateDirectory(_runtimeLog.LogDirectory);
            MacPlatformIntegration.OpenPath(_runtimeLog.LogDirectory);
        }
        catch (Exception exception)
        {
            _runtimeLog.WriteError("无法打开日志目录", exception);
            await UiDialogs.ShowMessageAsync(
                this,
                "无法打开日志目录",
                exception.Message);
        }
    }

    private void CloseWindow() => Close();

    private static Button CreateButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 32,
            Padding = new Thickness(12, 4)
        };
        button.Click += (_, _) => action();
        return button;
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

    private static void AddAtRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private static bool PathsEqual(string left, string? right) =>
        right is not null && string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private sealed record LogFileItem(string Path)
    {
        public override string ToString() => System.IO.Path.GetFileName(Path);
    }
}
