using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;

namespace MZTools;

internal static class MapInteraction
{
    internal static readonly DependencyProperty IsRelatedProperty = DependencyProperty.RegisterAttached(
        "IsRelated", typeof(bool), typeof(MapInteraction), new FrameworkPropertyMetadata(false));
    internal static void SetIsRelated(DataGridRow row, bool value) => row.SetValue(IsRelatedProperty, value);
    internal static int Direction(Key key) => key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
    internal static bool MoveGridSelection(DataGrid grid, Key key)
    {
        int direction = Direction(key);
        if (direction == 0 || grid.Items.Count == 0) return false;
        int current = grid.SelectedIndex;
        int next = current < 0 ? (direction > 0 ? 0 : grid.Items.Count - 1) : Math.Clamp(current + direction, 0, grid.Items.Count - 1);
        grid.SelectedItem = grid.Items[next];
        // A selection callback may reject the move (e.g. pending hex changes).
        if (grid.SelectedItem != null) grid.ScrollIntoView(grid.SelectedItem);
        return true;
    }
    internal static void EmphasizeSelection(DataGrid grid)
    {
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            for (var element = e.OriginalSource as DependencyObject; element != null && element != grid;
                element = element is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
                if (element is TextBox or ComboBox) return;
            grid.UnselectAll(); grid.UnselectAllCells(); e.Handled = true;
        };
        grid.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (DiskMapVisuals.IsBlankClick(e.OriginalSource as DependencyObject))
            { grid.UnselectAll(); grid.UnselectAllCells(); }
        };
        // Explicit triggers keep the synchronized selection visible when the map,
        // rather than the list, has keyboard focus. Respect system/high-contrast colors.
        Style Selected(Type target)
        {
            var style = new Style(target);
            var related = new DataTrigger { Value = true, Binding = target == typeof(DataGridRow)
                ? new Binding { RelativeSource = RelativeSource.Self, Path = new PropertyPath("(0)", IsRelatedProperty) }
                : new Binding { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridRow), 1),
                    Path = new PropertyPath("(0)", IsRelatedProperty) } };
            related.Setters.Add(new Setter(Control.BackgroundProperty, DiskMapVisuals.Selection));
            related.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.ControlTextBrush));
            style.Triggers.Add(related);
            var selected = new Trigger { Property = target == typeof(DataGridRow) ? DataGridRow.IsSelectedProperty : DataGridCell.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey)));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.HighlightTextBrushKey)));
            selected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            style.Triggers.Add(selected); return style;
        }
        grid.RowStyle = Selected(typeof(DataGridRow)); grid.CellStyle = Selected(typeof(DataGridCell));
    }
}
