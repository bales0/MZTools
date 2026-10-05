using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

// Continuous track, wrapped over eight rows. Small headers remain selectable in the block list.
public sealed class QuickDiskMapControl : FrameworkElement
{
    private QuickDiskLayout? layout;
    private QuickDiskRegion? selection;
    private readonly HashSet<int> selectedFiles = new();
    private double zoom = 1;
    private const int Rows = 8;
    private const double LabelWidth = 110, RowHeight = 31, BaseWidth = 600;
    public QuickDiskMapControl() { Focusable = true; }
    internal event Action<QuickDiskRegion?>? RegionSelected;
    internal void SetLayout(QuickDiskLayout? value)
    {
        layout = value; selection = null; selectedFiles.Clear();
        InvalidateMeasure(); InvalidateVisual();
    }
    internal void SetZoom(double value) { zoom = value; InvalidateMeasure(); InvalidateVisual(); }
    internal void Select(QuickDiskRegion? region) { selection = region; InvalidateVisual(); }
    internal void HighlightFiles(IEnumerable<int> indices)
    {
        selectedFiles.Clear(); selectedFiles.UnionWith(indices); InvalidateVisual();
    }
    protected override Size MeasureOverride(Size availableSize) => new(BaseWidth * zoom, (Rows + 1) * RowHeight);
    private double MapWidth => Math.Max(BaseWidth * zoom, RenderSize.Width);
    private double TrackWidth => MapWidth - LabelWidth - 12;

    internal static Brush Color(QuickDiskRegionKind kind) => kind switch
    {
        QuickDiskRegionKind.Count => Brushes.MediumPurple,
        QuickDiskRegionKind.Header => DiskMapVisuals.Metadata,
        QuickDiskRegionKind.Payload => DiskMapVisuals.Data,
        QuickDiskRegionKind.HostBlock or QuickDiskRegionKind.HostSector => DiskMapVisuals.Data,
        QuickDiskRegionKind.Framing or QuickDiskRegionKind.Crc => Brushes.Khaki,
        QuickDiskRegionKind.OutsideWindow => Brushes.DimGray,
        _ => Brushes.LightGray
    };
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(SystemColors.WindowBrush, null, new Rect(RenderSize));
        if (layout == null || layout.Length == 0) return;
        Text(dc, layout.IsPhysical ? "Track bitcells" : "File bytes", 3, 4);
        Text(dc, "Headers: orange | Data: green | CRC: yellow | Gaps: gray | Selected: blue", LabelWidth, 4);
        for (int row = 0; row < Rows; row++)
        {
            double start = layout.Length * (double)row / Rows, end = layout.Length * (double)(row + 1) / Rows;
            double y = (row + 1) * RowHeight;
            Text(dc, $"0x{(long)start:X}", 3, y + 6);
            var highlightedRegions = new List<(QuickDiskRegion Region, Rect Rectangle)>();
            foreach (var region in layout.Regions.Where(r => r.End > start && r.Start < end))
            {
                double x = LabelWidth + (Math.Max(start, region.Start) - start) / (end - start) * TrackWidth;
                double width = (Math.Min(end, region.End) - Math.Max(start, region.Start)) / (end - start) * TrackWidth;
                var rectangle = new Rect(x, y, Math.Max(0.5, width), RowHeight - 5);
                // A concrete block selection takes precedence over the synchronized file selection.
                bool highlighted = region == selection || (selection == null && region.FileIndex >= 0 && selectedFiles.Contains(region.FileIndex));
                // Keep the true coordinates; outline even tiny unselected frames.
                dc.DrawRectangle(highlighted ? DiskMapVisuals.Selection : Color(region.Kind), new Pen(Brushes.DarkSlateGray, 1), rectangle);
                if (highlighted) highlightedRegions.Add((region, rectangle));
            }
            dc.DrawRectangle(null, new Pen(SystemColors.ControlDarkBrush, 1), new Rect(LabelWidth, y, TrackWidth, RowHeight - 5));
            // Draw selection borders last so a following tiny region cannot cover them.
            foreach (var (region, rectangle) in highlightedRegions)
            {
                DiskMapVisuals.DrawSelection(dc, rectangle, region == selection);
            }
        }
    }
    private void Text(DrawingContext dc, string value, double x, double y) => dc.DrawText(
        new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
            SystemColors.WindowTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));

    internal QuickDiskRegion? Hit(Point point)
    {
        if (layout == null || point.X < LabelWidth || point.X >= LabelWidth + TrackWidth) return null;
        int row = (int)(point.Y / RowHeight) - 1;
        if (row < 0 || row >= Rows || point.Y % RowHeight >= RowHeight - 5) return null;
        double position = (row + (point.X - LabelWidth) / TrackWidth) * layout.Length / Rows;
        return layout.Regions.FirstOrDefault(r => r.Start <= position && r.End > position);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var region = Hit(e.GetPosition(this));
        ToolTip = region == null ? null : layout?.Detail(region);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        ClickAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int direction = MapInteraction.Direction(e.Key);
        if (direction != 0 && MoveSelection(direction)) e.Handled = true;
    }
    internal bool MoveSelection(int direction)
    {
        if (layout == null || layout.Regions.Count == 0) return false;
        var regions = layout.Regions.OrderBy(r => r.Start).ToArray();
        int current = Array.IndexOf(regions, selection);
        int next = current < 0 ? (direction > 0 ? 0 : regions.Length - 1) : Math.Clamp(current + Math.Sign(direction), 0, regions.Length - 1);
        Select(regions[next]); RegionSelected?.Invoke(regions[next]); return true;
    }

    internal void ClickAt(Point point)
    {
        var region = Hit(point);
        // A second click, or a click outside the track, clears both selection sources.
        if (region == selection) region = null;
        Select(region);
        RegionSelected?.Invoke(region);
    }
}
