using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Views;

public sealed partial class DuplicatesView : UserControl
{
    public DuplicatesView()
    {
        InitializeComponent();
    }

    private void OnDuplicateDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: not null } &&
            e.Source is Visual visual &&
            visual.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null &&
            DataContext is MainViewModel viewModel &&
            viewModel.OpenDuplicateLocationCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.OpenDuplicateLocationCommand.Execute(null);
        }
    }
}
