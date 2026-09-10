using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Views;

public sealed partial class DirectoriesView : UserControl
{
    public DirectoriesView()
    {
        InitializeComponent();
    }

    private void OnDirectoryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsDataRowDoubleTap(sender, e) &&
            DataContext is MainViewModel viewModel &&
            viewModel.OpenAssetDirectoryCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.OpenAssetDirectoryCommand.Execute(null);
        }
    }

    private static bool IsDataRowDoubleTap(object? sender, TappedEventArgs e) =>
        sender is DataGrid { SelectedItem: not null } &&
        e.Source is Visual visual &&
        visual.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null;
}
