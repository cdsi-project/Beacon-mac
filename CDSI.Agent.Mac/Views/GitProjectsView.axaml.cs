using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Views;

public sealed partial class GitProjectsView : UserControl
{
    public GitProjectsView()
    {
        InitializeComponent();
    }

    private void OnGitSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            DataContext is MainViewModel viewModel &&
            viewModel.SearchGitProjectsCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.SearchGitProjectsCommand.Execute(null);
        }
    }

    private void OnGitProjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: not null } &&
            e.Source is Visual visual &&
            visual.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null &&
            DataContext is MainViewModel viewModel &&
            viewModel.OpenGitProjectCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.OpenGitProjectCommand.Execute(null);
        }
    }
}
