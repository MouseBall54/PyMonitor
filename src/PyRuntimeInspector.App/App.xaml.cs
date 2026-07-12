using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PyRuntimeInspector.App;

public partial class App : Application
{
    private void DataGrid_OnPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid
            || e.ChangedButton != MouseButton.Right
            || e.OriginalSource is not DependencyObject source
            || FindVisualAncestor<DataGridCell>(source) is not { } cell)
        {
            return;
        }

        dataGrid.SelectedItem = cell.DataContext;
        dataGrid.CurrentCell = new DataGridCellInfo(cell);
        cell.Focus();
    }

    private void CopyableTextBlock_OnPreviewMouseRightButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not TextBlock textBlock || string.IsNullOrEmpty(textBlock.Text))
        {
            return;
        }

        var dataGrid = FindVisualAncestor<DataGrid>(textBlock);
        if (dataGrid is not null && FindVisualAncestor<DataGridCell>(textBlock) is { } cell)
        {
            dataGrid.SelectedItem = cell.DataContext;
            dataGrid.CurrentCell = new DataGridCellInfo(cell);
            cell.Focus();
        }

        var menu = new ContextMenu
        {
            PlacementTarget = textBlock,
        };
        menu.Items.Add(new MenuItem
        {
            Header = "Copy value",
            Command = global::PyRuntimeInspector.App.MainWindow.CopyDisplayedValueCommand,
            CommandParameter = textBlock.Text,
            CommandTarget = Current?.MainWindow as IInputElement ?? textBlock,
        });
        if (dataGrid is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = "Copy selected cells",
                Command = ApplicationCommands.Copy,
                CommandTarget = dataGrid,
            });
        }

        textBlock.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static T? FindVisualAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var current = element; current is not null; current = VisualParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static DependencyObject? VisualParent(DependencyObject element) => element switch
    {
        Visual => VisualTreeHelper.GetParent(element),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(element),
    };
}
