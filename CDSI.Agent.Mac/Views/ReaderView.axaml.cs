using Avalonia.Controls;
using Avalonia.Input;
using CDSI.Agent.Mac.ViewModels;

namespace CDSI.Agent.Mac.Views;

public sealed partial class ReaderView : UserControl
{
    public ReaderView()
    {
        InitializeComponent();
    }

    private void OnReaderSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var command = viewModel.SearchReaderEntriesCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    private void OnReaderEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var command = viewModel.OpenReaderEntryCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
