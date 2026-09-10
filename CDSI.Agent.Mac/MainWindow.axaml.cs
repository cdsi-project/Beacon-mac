using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CDSI.Agent.Mac.ViewModels;
using CDSI.Agent.Mac.Views;

namespace CDSI.Agent.Mac;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(
            InputElement.KeyDownEvent,
            OnWindowKeyDown,
            RoutingStrategies.Tunnel);
    }

    private void OnCloseWindowClick(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnFocusAssetSearchClick(object? sender, EventArgs e)
    {
        FocusAssetSearch();
    }

    private bool FocusAssetSearch()
    {
        var tabs = this.FindControl<TabControl>("MainTabs");
        if (tabs is null)
        {
            return false;
        }

        tabs.SelectedIndex = 0;
        if (tabs.SelectedContent is AssetsView assetsView)
        {
            assetsView.FocusSearchBox();
            return true;
        }

        return false;
    }

    private void OnSelectTabClick(object? sender, EventArgs e)
    {
        if (sender is not NativeMenuItem { CommandParameter: string value } ||
            !int.TryParse(value, out var index))
        {
            return;
        }

        var tabs = this.FindControl<TabControl>("MainTabs");
        if (tabs is not null && index >= 0 && index < tabs.ItemCount)
        {
            tabs.SelectedIndex = index;
        }
    }

    internal IReadOnlyList<AssetRowViewModel> GetSelectedAssets()
    {
        var selected = this.FindControl<AssetsView>("AssetsPage")?.GetSelectedAssets();
        if (selected is { Count: > 0 })
        {
            return selected;
        }

        return DataContext is MainViewModel { SelectedAsset: { } current }
            ? [current]
            : [];
    }

    internal IReadOnlyList<ProjectRowViewModel> GetSelectedProjects()
    {
        var selected = this.FindControl<ProjectsView>("ProjectsPage")?.GetSelectedProjects();
        if (selected is { Count: > 0 })
        {
            return selected;
        }

        return DataContext is MainViewModel { SelectedProject: { } current }
            ? [current]
            : [];
    }

    internal IReadOnlyList<ProjectAssetRowViewModel> GetSelectedProjectAssets()
    {
        var selected = this.FindControl<ProjectsView>("ProjectsPage")?
            .GetSelectedProjectAssets();
        if (selected is { Count: > 0 })
        {
            return selected;
        }

        return DataContext is MainViewModel { SelectedProjectAsset: { } current }
            ? [current]
            : [];
    }

    internal IReadOnlyList<CloudBackupRowViewModel> GetSelectedCloudBackups()
    {
        var selected = this.FindControl<CloudBackupsView>("CloudBackupsPage")?
            .GetSelectedBackups();
        if (selected is { Count: > 0 })
        {
            return selected;
        }

        return DataContext is MainViewModel { SelectedCloudBackup: { } current }
            ? [current]
            : [];
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var assetsView = this.FindControl<AssetsView>("AssetsPage");
        var projectsView = this.FindControl<ProjectsView>("ProjectsPage");
        var primaryShortcut = IsPrimaryShortcut(e.KeyModifiers);
        var handled = false;

        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        {
            handled = Execute(viewModel.RefreshCommand);
        }
        else if (e.Key == Key.F6 && primaryShortcut)
        {
            handled = Execute(viewModel.StartFullScanCommand);
        }
        else if (e.Key == Key.F6 && e.KeyModifiers == KeyModifiers.None)
        {
            handled = Execute(viewModel.StartStandardScanCommand);
        }
        else if (TryGetTabShortcut(e.Key, e.KeyModifiers, out var tabIndex))
        {
            handled = SelectTab(tabIndex);
        }
        else if (e.Key == Key.J && primaryShortcut)
        {
            handled = Execute(viewModel.ShowTaskCenterCommand);
        }
        else if (e.Key == Key.OemComma && primaryShortcut)
        {
            handled = Execute(viewModel.OpenSettingsCommand);
        }
        else if (e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.None)
        {
            handled = Execute(viewModel.OpenDocumentationCommand);
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            handled = Execute(viewModel.CancelCurrentTaskCommand);
        }
        else if (e.Key == Key.F && primaryShortcut && !viewModel.IsBusy)
        {
            handled = FocusAssetSearch();
        }
        else if (e.Key == Key.N && primaryShortcut &&
                 viewModel.CreateProjectCommand.CanExecute(null))
        {
            SelectTab(3);
            viewModel.CreateProjectCommand.Execute(null);
            handled = true;
        }
        else if (e.Key == Key.A && primaryShortcut &&
                 assetsView?.IsAssetGridFocused == true)
        {
            handled = assetsView.SelectAllAssets();
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt &&
                 assetsView?.IsAssetGridFocused == true)
        {
            handled = Execute(viewModel.ShowAssetDetailsCommand);
        }
        else if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift &&
                 !viewModel.IsBusy)
        {
            handled = TryOpenFocusedContextMenu();
        }
        else if (e.Key == Key.Tab && IsPrimaryShiftShortcut(e.KeyModifiers))
        {
            handled = SelectAdjacentTab(previous: true);
        }
        else if (e.Key == Key.Tab && primaryShortcut)
        {
            handled = SelectAdjacentTab(previous: false);
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None &&
                 assetsView?.IsAssetGridFocused == true)
        {
            handled = Execute(viewModel.OpenAssetLocationCommand);
        }
        else if (e.Key is Key.Delete or Key.Back &&
                 e.KeyModifiers == KeyModifiers.None)
        {
            if (assetsView?.IsAssetGridFocused == true)
            {
                handled = Execute(viewModel.RemoveAssetsCommand);
            }
            else if (projectsView?.IsProjectGridFocused == true)
            {
                handled = Execute(viewModel.DeleteProjectCommand);
            }
        }

        e.Handled = handled;
    }

    private bool SelectAdjacentTab(bool previous)
    {
        var tabs = this.FindControl<TabControl>("MainTabs");
        if (tabs is null || tabs.ItemCount <= 1)
        {
            return false;
        }

        var current = tabs.SelectedIndex >= 0 && tabs.SelectedIndex < tabs.ItemCount
            ? tabs.SelectedIndex
            : 0;
        tabs.SelectedIndex = previous
            ? (current + tabs.ItemCount - 1) % tabs.ItemCount
            : (current + 1) % tabs.ItemCount;
        return true;
    }

    private bool SelectTab(int index)
    {
        var tabs = this.FindControl<TabControl>("MainTabs");
        if (tabs is null || index < 0 || index >= tabs.ItemCount)
        {
            return false;
        }

        tabs.SelectedIndex = index;
        return true;
    }

    private bool TryOpenFocusedContextMenu()
    {
        if (FocusManager?.GetFocusedElement() is not Visual focused)
        {
            return false;
        }

        var grid = focused.FindAncestorOfType<DataGrid>(includeSelf: true);
        if (grid is not { SelectedItem: not null, ContextMenu: { } contextMenu })
        {
            return false;
        }

        contextMenu.Open(grid);
        return true;
    }

    private static bool IsPrimaryShortcut(KeyModifiers modifiers) =>
        modifiers is KeyModifiers.Meta or KeyModifiers.Control;

    private static bool IsPrimaryShiftShortcut(KeyModifiers modifiers) =>
        modifiers == (KeyModifiers.Meta | KeyModifiers.Shift) ||
        modifiers == (KeyModifiers.Control | KeyModifiers.Shift);

    private static bool TryGetTabShortcut(
        Key key,
        KeyModifiers modifiers,
        out int index)
    {
        index = key switch
        {
            Key.D1 => 0,
            Key.D2 => 1,
            Key.D3 => 2,
            Key.D4 => 3,
            Key.D5 => 4,
            Key.D6 => 5,
            Key.D7 => 6,
            Key.D8 => 7,
            _ => -1
        };
        return index >= 0 && IsPrimaryShortcut(modifiers);
    }

    private static bool Execute(ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }
}
