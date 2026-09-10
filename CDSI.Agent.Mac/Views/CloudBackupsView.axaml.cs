using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Views;

public sealed partial class CloudBackupsView : UserControl
{
    public CloudBackupsView()
    {
        InitializeComponent();
    }

    public IReadOnlyList<CloudBackupRowViewModel> GetSelectedBackups()
    {
        var grid = this.FindControl<DataGrid>("CloudBackupGrid");
        var selected = grid?.SelectedItems?.OfType<CloudBackupRowViewModel>().ToArray() ?? [];
        if (selected.Length > 0)
        {
            return selected;
        }

        return grid?.SelectedItem is CloudBackupRowViewModel current ? [current] : [];
    }

    private void OnCloudBackupSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            DataContext is MainViewModel viewModel &&
            viewModel.SearchCloudBackupsCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.SearchCloudBackupsCommand.Execute(null);
        }
    }

    private void OnCloudBackupDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: not null } &&
            e.Source is Visual visual &&
            visual.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null &&
            DataContext is MainViewModel viewModel &&
            viewModel.OpenCloudBackupLocationCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.OpenCloudBackupLocationCommand.Execute(null);
        }
    }
}
