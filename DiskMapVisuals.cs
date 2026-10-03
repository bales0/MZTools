using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MZTools;

internal static class DiskMapVisuals
{
    internal static Brush Selection => Brushes.CornflowerBlue;
    internal static Brush Metadata => Brushes.DarkOrange;
    internal static Brush Data => Brushes.MediumSeaGreen;

    internal static void DrawSelection(DrawingContext dc, Rect rectangle, bool primary)
    {
        dc.DrawRectangle(null, new Pen(Brushes.Black, primary ? 3 : 2), rectangle);
        if (rectangle.Width < 5 || rectangle.Height < 5) return;
        rectangle.Inflate(-1.5, -1.5);
        dc.DrawRectangle(null, new Pen(Brushes.White, 1), rectangle);
    }

    // Blank space includes the surrounding panel, not just the drawing surface.
    // Do not interfere with selection, buttons, scrolling, resizing or text copying.
    internal static bool IsBlankClick(DependencyObject? source)
    {
        for (var current = source; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is QuickDiskMapControl or DskDiskMapControl or DataGridRow or DataGridColumnHeader or
                ButtonBase or RangeBase or Thumb or TextBoxBase or TabItem)
                return false;
        return true;
    }
}
