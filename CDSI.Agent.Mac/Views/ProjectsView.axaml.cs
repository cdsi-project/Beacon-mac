using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Views;

public sealed partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
    }

    public IReadOnlyList<ProjectRowViewModel> GetSelectedProjects() =>
        GetSelected<ProjectRowViewModel>("ProjectGrid");

    public IReadOnlyList<ProjectAssetRowViewModel> GetSelectedProjectAssets() =>
        GetSelected<ProjectAssetRowViewModel>("ProjectAssetGrid");

    internal bool IsProjectGridFocused =>
        this.FindControl<DataGrid>("ProjectGrid")?.IsKeyboardFocusWithin == true;

    private IReadOnlyList<T> GetSelected<T>(string gridName) where T : class
    {
        var grid = this.FindControl<DataGrid>(gridName);
        var selected = grid?.SelectedItems?.OfType<T>().ToArray() ?? [];
        if (selected.Length > 0)
        {
            return selected;
        }

        return grid?.SelectedItem is T current ? [current] : [];
    }

    private void OnProjectDoubleTapped(object? sender, TappedEventArgs e)
    {
        ExecuteRowCommand(sender, e, viewModel => viewModel.EditProjectCommand);
    }

    private void OnProjectAssetDoubleTapped(object? sender, TappedEventArgs e)
    {
        ExecuteRowCommand(
            sender,
            e,
            viewModel => viewModel.OpenProjectAssetLocationCommand);
    }

    private void ExecuteRowCommand(
        object? sender,
        TappedEventArgs e,
        Func<MainViewModel, System.Windows.Input.ICommand> selectCommand)
    {
        if (sender is not DataGrid { SelectedItem: not null } ||
            e.Source is not Visual visual ||
            visual.FindAncestorOfType<DataGridRow>(includeSelf: true) is null ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var command = selectCommand(viewModel);
        if (command.CanExecute(null))
        {
            e.Handled = true;
            command.Execute(null);
        }
    }
}
