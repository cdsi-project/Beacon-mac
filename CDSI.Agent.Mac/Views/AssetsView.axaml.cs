using Avalonia.Controls;
using Avalonia.Input;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Views;

public sealed partial class AssetsView : UserControl
{
    public AssetsView()
    {
        InitializeComponent();
    }

    public void FocusSearchBox()
    {
        var searchBox = this.FindControl<TextBox>("AssetSearchBox");
        searchBox?.Focus();
        searchBox?.SelectAll();
    }

    public IReadOnlyList<AssetRowViewModel> GetSelectedAssets()
    {
        var grid = this.FindControl<DataGrid>("AssetGrid");
        var selected = grid?.SelectedItems?.OfType<AssetRowViewModel>().ToArray() ?? [];
        if (selected.Length > 0)
        {
            return selected;
        }

        return grid?.SelectedItem is AssetRowViewModel current ? [current] : [];
    }

    internal bool IsAssetGridFocused =>
        this.FindControl<DataGrid>("AssetGrid")?.IsKeyboardFocusWithin == true;

    internal bool SelectAllAssets()
    {
        var grid = this.FindControl<DataGrid>("AssetGrid");
        var items = grid?.ItemsSource?.Cast<object>().ToArray() ?? [];
        if (grid is null || items.Length == 0)
        {
            return false;
        }

        grid.SelectedItems.Clear();
        foreach (var item in items)
        {
            grid.SelectedItems.Add(item);
        }

        return true;
    }

    private void OnAssetSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            DataContext is MainViewModel viewModel &&
            viewModel.SearchAssetsCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.SearchAssetsCommand.Execute(null);
        }
    }
}
