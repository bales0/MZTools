using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

// One lightweight drawing surface; sectors are not individual WPF controls.
public sealed class DskDiskMapControl : FrameworkElement
{
    private DskLayoutModel? layout;
    private readonly HashSet<string> selectedFiles = new();
    private DskSectorLayout? selectedSector;
    private double zoom = 1;
    private bool logical;
    private const double LabelWidth = 115, CellWidth = 38, RowHeight = 25;
    internal event Action<DskSectorLayout?>? SectorSelected;
    internal void SetLayout(DskLayoutModel? value) { layout = value; selectedSector = null; selectedFiles.Clear(); InvalidateMeasure(); InvalidateVisual(); }
    internal void SetZoom(double value) { zoom = value; InvalidateMeasure(); InvalidateVisual(); }
    internal void SetLogical(bool value) { logical = value; InvalidateVisual(); }
    internal void HighlightFiles(IEnumerable<string> keys) { selectedFiles.Clear(); selectedFiles.UnionWith(keys); InvalidateVisual(); }
    internal void SelectSector(DskSectorLayout? value) { selectedSector = value; InvalidateVisual(); if (value != null) BringIntoView(new Rect(0, (value.Track + 1) * RowHeight * zoom, RenderSize.Width, RowHeight * zoom)); }
    private IEnumerable<DskSectorLayout> Ordered(DskTrackLayout track) => logical
        ? track.Sectors.OrderBy(s => s.LogicalBlocks.Count == 0 ? int.MaxValue : s.LogicalBlocks.Min()).ThenBy(s => s.PhysicalIndex)
        : track.Sectors;

    protected override Size MeasureOverride(Size availableSize) => new(
        (LabelWidth + CellWidth * Math.Max(1, layout?.Tracks.Select(t => t.Sectors.Count).DefaultIfEmpty().Max() ?? 1)) * zoom,
        ((layout?.Tracks.Count ?? 0) + 1) * RowHeight * zoom);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(SystemColors.WindowBrush, null, new Rect(RenderSize));
        if (layout == null) return;
        Text(dc, logical ? "Logical order*" : "Physical order", 3, 3);
        for (int col = 0; col < layout.Tracks.Select(t => t.Sectors.Count).DefaultIfEmpty().Max(); col++)
            Text(dc, col.ToString(), LabelWidth + col * CellWidth + 4, 3);
        foreach (var track in layout.Tracks)
        {
            double y = (track.PhysicalIndex + 1) * RowHeight;
            Text(dc, $"{track.PhysicalIndex}: C{track.Cylinder} H{track.Side}", 3, y + 3);
            if (track.IsMissing) { Text(dc, "Missing track (tsize=0)", LabelWidth, y + 3); continue; }
            int col = 0;
            foreach (var sector in Ordered(track))
            {
                var rect = new Rect((LabelWidth + col * CellWidth) * zoom, y * zoom, (CellWidth - 2) * zoom, (RowHeight - 2) * zoom);
                bool highlight = sector == selectedSector || sector.Owners.Any(o => selectedFiles.Contains(o.FileKey)) || selectedFiles.Contains($"{sector.Track}:{sector.PhysicalIndex}");
                Brush fill = highlight ? DiskMapVisuals.Selection : sector.HasIssue ? Brushes.Khaki :
                    (sector.Role & (DskSectorRole.Boot | DskSectorRole.System | DskSectorRole.Directory | DskSectorRole.Fat | DskSectorRole.AllocationMap)) != 0 ? DiskMapVisuals.Metadata :
                    sector.Role.HasFlag(DskSectorRole.Data) ? DiskMapVisuals.Data : SystemColors.WindowBrush;
                dc.DrawRectangle(fill, new Pen(Brushes.DarkSlateGray, 1), rect);
                if (highlight) DiskMapVisuals.DrawSelection(dc, rect, sector == selectedSector);
                Text(dc, $"{(sector.HasIssue ? "!" : "")}{sector.R}", LabelWidth + col * CellWidth + 3, y + 3,
                    highlight ? Brushes.Black : sector.HasIssue ? SystemColors.InfoTextBrush :
                    fill == SystemColors.WindowBrush ? SystemColors.WindowTextBrush : SystemColors.ControlTextBrush);
                col++;
            }
        }
    }
    private void Text(DrawingContext dc, string value, double x, double y, Brush? brush = null) => dc.DrawText(
        new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11 * zoom,
            brush ?? SystemColors.WindowTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x * zoom, y * zoom));

    private DskSectorLayout? Hit(Point p)
    {
        if (layout == null) return null;
        int row = (int)(p.Y / (RowHeight * zoom)) - 1, col = (int)((p.X / zoom - LabelWidth) / CellWidth);
        if (p.X / zoom < LabelWidth || row < 0 || row >= layout.Tracks.Count || col < 0) return null;
        return Ordered(layout.Tracks[row]).ElementAtOrDefault(col);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); ToolTip = Hit(e.GetPosition(this))?.Detail; }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var sector = Hit(e.GetPosition(this));
        if (sector == selectedSector) sector = null;
        selectedSector = sector; InvalidateVisual(); SectorSelected?.Invoke(sector); e.Handled = true;
    }
}
