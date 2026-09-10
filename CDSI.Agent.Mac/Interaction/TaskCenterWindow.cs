using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Interaction;

public sealed class TaskCenterWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TextBlock _stateText = new();
    private readonly TextBlock _progressText = new();
    private readonly TextBlock _currentItemText = new();
    private readonly TextBlock _errorText = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _cancelButton = new();
    private bool _detached;

    public TaskCenterWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        Title = "任务中心";
        Width = 660;
        Height = 330;
        MinWidth = 520;
        MinHeight = 280;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _stateText.FontSize = 18;
        _stateText.FontWeight = Avalonia.Media.FontWeight.SemiBold;
        _progressText.Foreground = Avalonia.Media.Brushes.DimGray;
        _currentItemText.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _currentItemText.MaxHeight = 72;
        _errorText.Foreground = Avalonia.Media.Brushes.Firebrick;
        _errorText.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Height = 7;

        _cancelButton.Content = "取消当前任务";
        _cancelButton.MinHeight = 32;
        _cancelButton.Padding = new Thickness(12, 4);
        _cancelButton.Click += (_, _) =>
        {
            if (_viewModel.CancelCurrentTaskCommand.CanExecute(null))
            {
                _viewModel.CancelCurrentTaskCommand.Execute(null);
            }
        };
        var closeButton = new Button
        {
            Content = "关闭",
            MinHeight = 32,
            MinWidth = 88,
            Padding = new Thickness(12, 4)
        };
        closeButton.Click += (_, _) => Close();

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        footer.Children.Add(_cancelButton);
        Grid.SetColumn(closeButton, 2);
        footer.Children.Add(closeButton);

        var layout = new Grid
        {
            Margin = new Thickness(22),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"),
            RowSpacing = 10
        };
        AddAtRow(layout, _stateText, 0);
        AddAtRow(layout, _progressText, 1);
        AddAtRow(layout, _progress, 2);
        AddAtRow(layout, _currentItemText, 3);
        AddAtRow(layout, _errorText, 4);
        AddAtRow(layout, footer, 5);
        Content = layout;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => Detach();
        UpdateDisplay();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateDisplay);
    }

    private void UpdateDisplay()
    {
        if (_detached)
        {
            return;
        }

        _stateText.Text = _viewModel.StatusText;
        _progressText.Text = _viewModel.IsBusy
            ? _viewModel.ProgressText
            : "当前没有正在运行的任务";
        _currentItemText.Text = string.IsNullOrWhiteSpace(_viewModel.CurrentPath)
            ? string.Empty
            : $"当前项目：{_viewModel.CurrentPath}";
        _errorText.Text = string.IsNullOrWhiteSpace(_viewModel.LastError)
            ? string.Empty
            : $"最近错误：{_viewModel.LastError}";
        _progress.IsIndeterminate = _viewModel.IsBusy &&
            _viewModel.IsProgressIndeterminate;
        _progress.Value = _viewModel.ProgressValue;
        _cancelButton.IsEnabled =
            _viewModel.CancelCurrentTaskCommand.CanExecute(null);
    }

    private void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private static void AddAtRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }
}
