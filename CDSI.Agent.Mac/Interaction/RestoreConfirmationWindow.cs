using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Mac.Interaction;

internal sealed class RestoreConfirmationWindow : Window
{
    private readonly IReadOnlyList<RestoreRow> _rows;
    private readonly RadioButton _workspaceChoice = new();
    private readonly RadioButton _directoryChoice = new();
    private readonly TextBox _directoryPath = new();
    private readonly Button _browseButton = new();
    private readonly string? _workspacePath;

    public RestoreConfirmationWindow(
        IReadOnlyList<ObjectStorageRestoreCandidate> candidates,
        string? workspacePath)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || candidates.Any(item => item.Sources.Count == 0))
        {
            throw new ArgumentException(
                "每个待取回资产都必须至少有一个可用来源。",
                nameof(candidates));
        }

        _workspacePath = string.IsNullOrWhiteSpace(workspacePath)
            ? null
            : Path.GetFullPath(workspacePath);
        _rows = candidates.Select(CreateRestoreRow).ToArray();

        Title = "从云端取回资产";
        Width = 940;
        Height = 620;
        MinWidth = 720;
        MinHeight = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        Content = BuildContent();
        UpdateDestinationState();
    }

    public IReadOnlyList<ObjectStorageRestoreRequest> SelectedRequests { get; private set; } = [];

    public ObjectStorageRestoreDestination? Destination { get; private set; }

    private Control BuildContent()
    {
        _workspaceChoice.Content = "CDSI 工作目录";
        _workspaceChoice.GroupName = "restore-destination";
        _workspaceChoice.IsEnabled = _workspacePath is not null;
        _workspaceChoice.IsChecked = _workspacePath is not null;
        _workspaceChoice.IsCheckedChanged += (_, _) => UpdateDestinationState();
        _directoryChoice.Content = "用户指定目录";
        _directoryChoice.GroupName = "restore-destination";
        _directoryChoice.IsChecked = _workspacePath is null;
        _directoryChoice.IsCheckedChanged += (_, _) => UpdateDestinationState();
        _browseButton.Content = "选择...";
        _browseButton.MinWidth = 88;
        _browseButton.Click += async (_, _) => await BrowseAsync();

        var destinations = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("150,*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 8,
            RowSpacing = 8
        };
        destinations.Children.Add(_workspaceChoice);
        var workspaceText = new TextBlock
        {
            Text = _workspacePath is null
                ? "尚未配置"
                : Path.Combine(_workspacePath, "Assets"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.DimGray
        };
        Grid.SetColumn(workspaceText, 1);
        Grid.SetColumnSpan(workspaceText, 2);
        destinations.Children.Add(workspaceText);
        Grid.SetRow(_directoryChoice, 1);
        destinations.Children.Add(_directoryChoice);
        Grid.SetRow(_directoryPath, 1);
        Grid.SetColumn(_directoryPath, 1);
        destinations.Children.Add(_directoryPath);
        Grid.SetRow(_browseButton, 1);
        Grid.SetColumn(_browseButton, 2);
        destinations.Children.Add(_browseButton);

        var sourceRows = new StackPanel { Spacing = 10 };
        foreach (var row in _rows)
        {
            var item = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("220,*"),
                ColumnSpacing = 12
            };
            item.Children.Add(new TextBlock
            {
                Text = row.Candidate.OriginalFilename,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(row.SourcePicker, 1);
            item.Children.Add(row.SourcePicker);
            sourceRows.Children.Add(item);
            sourceRows.Children.Add(new Separator());
        }

        var cancel = CreateButton("取消");
        cancel.Click += (_, _) => Close(false);
        var confirm = CreateButton("开始取回", primary: true);
        confirm.Click += async (_, _) => await ConfirmAsync();
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm }
        };

        var layout = new Grid
        {
            Margin = new Thickness(22),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            RowSpacing = 12
        };
        AddAtRow(layout, new TextBlock
        {
            Text = $"从云端取回 {_rows.Count:N0} 个资产",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold
        }, 0);
        AddAtRow(layout, destinations, 1);
        AddAtRow(layout, new TextBlock
        {
            Text = "每个资产可选择一个已验证的云端来源。系统先下载到临时文件并校验 SHA-256；目标位置已有不同内容时不会覆盖。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#913131"))
        }, 2);
        AddAtRow(layout, new ScrollViewer
        {
            Content = sourceRows,
            VerticalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        }, 3);
        AddAtRow(layout, footer, 4);
        return layout;
    }

    private async Task BrowseAsync()
    {
        var path = await UiDialogs.ChooseFolderAsync(
            this,
            "选择云端资产取回目录",
            _directoryPath.Text);
        if (path is not null)
        {
            _directoryPath.Text = path;
            _directoryChoice.IsChecked = true;
        }
    }

    private async Task ConfirmAsync()
    {
        ObjectStorageRestoreDestination destination;
        if (_workspaceChoice.IsChecked == true)
        {
            destination = new ObjectStorageRestoreDestination(
                ObjectStorageRestoreDestinationKind.ManagedWorkspace);
        }
        else
        {
            string path;
            if (string.IsNullOrWhiteSpace(_directoryPath.Text))
            {
                await UiDialogs.ShowMessageAsync(
                    this,
                    "取回目录无效",
                    "请选择一个本地目录。");
                return;
            }

            try
            {
                path = Path.GetFullPath(_directoryPath.Text.Trim());
            }
            catch (Exception exception) when (exception is
                ArgumentException or NotSupportedException or PathTooLongException)
            {
                await UiDialogs.ShowMessageAsync(
                    this,
                    "取回目录无效",
                    "请选择一个有效的本地目录。");
                return;
            }

            if (!Directory.Exists(path))
            {
                await UiDialogs.ShowMessageAsync(
                    this,
                    "取回目录无效",
                    "所选本地目录不存在。");
                return;
            }

            destination = new ObjectStorageRestoreDestination(
                ObjectStorageRestoreDestinationKind.SelectedDirectory,
                path);
        }

        var requests = new List<ObjectStorageRestoreRequest>(_rows.Count);
        foreach (var row in _rows)
        {
            if (row.SourcePicker.SelectedItem is not RestoreSourceOption source)
            {
                await UiDialogs.ShowMessageAsync(
                    this,
                    "请选择取回来源",
                    "请为每个资产选择一个可用的云端来源。");
                return;
            }

            requests.Add(new ObjectStorageRestoreRequest(
                row.Candidate.AssetId,
                source.Source.Source.Location.Id));
        }

        SelectedRequests = requests;
        Destination = destination;
        Close(true);
    }

    private void UpdateDestinationState()
    {
        var selectedDirectory = _directoryChoice.IsChecked == true;
        _directoryPath.IsEnabled = selectedDirectory;
        _browseButton.IsEnabled = selectedDirectory;
    }

    private static RestoreRow CreateRestoreRow(
        ObjectStorageRestoreCandidate candidate)
    {
        var choices = candidate.Sources
            .Select(source => new RestoreSourceOption(source))
            .ToArray();
        return new RestoreRow(
            candidate,
            new ComboBox
            {
                ItemsSource = choices,
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
    }

    private static Button CreateButton(string text, bool primary = false) => new()
    {
        Content = text,
        MinWidth = 88,
        MinHeight = 32,
        Padding = new Thickness(14, 4),
        Background = primary
            ? new SolidColorBrush(Color.Parse("#18794E"))
            : Brushes.Transparent,
        Foreground = primary ? Brushes.White : Brushes.Black
    };

    private static void AddAtRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private sealed record RestoreRow(
        ObjectStorageRestoreCandidate Candidate,
        ComboBox SourcePicker);

    private sealed record RestoreSourceOption(
        ConfiguredObjectStorageRestoreSource Source)
    {
        public override string ToString() =>
            $"{Source.Profile.DisplayName} · {Source.Profile.Provider} · " +
            $"{Source.Profile.BucketName} · {Source.Source.Location.ObjectKey}";
    }
}
