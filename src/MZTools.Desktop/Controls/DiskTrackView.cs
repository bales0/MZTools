using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Controls;

// Single source of bitcells is the existing Core PhysicalTrack. The high zoom
// paints individual clock/data cells and a step waveform; it is NOT measured flux.
public sealed class DiskTrackView : Control
{
    public DiskSession? Session { get; set; }
    internal event Action<DskSectorAddress>? SectorSelected;
    private Point? press;
    private double startAtPress;
    private bool moved;
    private const double Left = 16;
    private double PlotWidth => Math.Max(1, Bounds.Width - 32);
    public DiskTrackView() { Focusable = true; ClipToBounds = true; MinHeight = 176; }
    private IBrush Brush(string name) => this.FindResource("F2." + name) as IBrush ?? Brushes.Black;
    private void Label(DrawingContext c, string text, double x, double y, double size = 11, IBrush? brush = null)
        => c.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), size, brush ?? Brush("text")), new Point(x, y));
    private IReadOnlyList<DskGeneratedRegion> Regions(PhysicalTrack t)
        => Session?.Snapshot.IsPhysical == true ? DskGeneratedProjection.StoredRegions(t) : DskGeneratedProjection.Regions(t);
    private static byte ReadByte(PhysicalTrack t, long first)
    {
        byte value = 0;
        for (int b = 0; b < 8; b++)
            value = (byte)((value << 1) | (PackedBitCells.Get(t.PackedBitCells, checked((int)(first + b * 2 + 1))) ? 1 : 0));
        return value;
    }
    private IBrush RegionColor(string kind) => kind switch
    {
        "DATA" => Brush("healthy-bg"),
        "Sync" => Brush("disk-unassigned"),
        _ when kind.Contains("CRC", StringComparison.OrdinalIgnoreCase) => Brush("selected"),
        _ when kind.Contains("AM", StringComparison.OrdinalIgnoreCase) => Brush("warning-bg"),
        _ => Brush("disk-free")
    };
    public void Fit()
    {
        if (Session is not { TrackData: { } t } s) return;
        s.CellStart = 0; s.CellLength = Math.Max(1, t.BitCellCount); InvalidateVisual();
    }
    public void FitSector(int descriptor)
    {
        if (Session is not { TrackData: { } t } s || descriptor < 0 || descriptor >= t.Sectors.Count) return;
        var sector = t.Sectors[descriptor];
        s.CellStart = Math.Max(0, sector.HeaderCell - 192);
        s.CellLength = Math.Max(1, Math.Min(t.BitCellCount - s.CellStart,
            sector.DataCell + sector.Data.Length * 16L + 32 - s.CellStart));
        InvalidateVisual();
    }
    public void Zoom(double factor, double anchor = .5)
    {
        if (Session is not { TrackData: { } t } s || !double.IsFinite(factor) || factor <= 0 || t.BitCellCount == 0) return;
        double old = Math.Clamp(s.CellLength, 1, t.BitCellCount);
        double next = Math.Clamp(old / factor, Math.Min(16, t.BitCellCount), t.BitCellCount);
        s.CellStart = Math.Clamp(s.CellStart + (old - next) * Math.Clamp(anchor, 0, 1), 0, t.BitCellCount - next);
        s.CellLength = next; InvalidateVisual();
    }
    public override void Render(DrawingContext c)
    {
        base.Render(c);
        c.DrawRectangle(Brush("surface"), new Pen(Brush("border")), new Rect(Bounds.Size));
        if (Session is not { TrackData: { } track } s || track.BitCellCount == 0)
        { Label(c, "Select a physical track and choose Load track", 12, 12); return; }
        double start = Math.Clamp(s.CellStart, 0, track.BitCellCount - 1);
        double length = Math.Clamp(s.CellLength, 1, track.BitCellCount - start);
        double end = start + length;
        double cellPixels = PlotWidth / length;
        double X(double cell) => Left + (cell - start) * cellPixels;
        bool stored = s.Snapshot.IsPhysical;
        Label(c, $"{(stored ? "STORED HFE" : "GENERATED MFM")} · T{s.TrackIndex} · cells {(long)start:N0}–{(long)end:N0}", 8, 5);
        // Regions are clipped to the visible interval; no full-track draw loops at high zoom.
        var regions = Regions(track);
        foreach (var region in regions.Where(r => r.EndCell > start && r.StartCell < end))
        {
            double a = Math.Max(Left, X(Math.Max(start, region.StartCell)));
            double b = Math.Min(Left + PlotWidth, X(Math.Min(end, region.EndCell)));
            if (b <= a) continue;
            c.DrawRectangle(RegionColor(region.Kind), null, new Rect(a, 29, b - a, 24));
            if (b - a > 68) Label(c, region.Kind, a + 2, 33, 10, Brushes.Black);
        }
        double high = Math.Max(85, Bounds.Height * .52);
        double low = Math.Min(Bounds.Height - 24, Math.Max(high + 15, Bounds.Height * .76));
        var signal = new Pen(Brush("accent"), 1.25);
        var weak = Brush("error");
        if (cellPixels < 3)
        {
            // Fast overview: bound aggregation work by the view width and source cells.
            int pixels = Math.Max(1, (int)PlotWidth);
            for (int x = 0; x < pixels; x++)
            {
                int first = (int)Math.Clamp(Math.Floor(start + length * x / pixels), 0, track.BitCellCount - 1);
                int last = (int)Math.Clamp(Math.Ceiling(start + length * (x + 1) / pixels), first + 1, track.BitCellCount);
                bool zero = false, one = false, isWeak = false;
                for (int cell = first; cell < last; cell++)
                {
                    bool bit = PackedBitCells.Get(track.PackedBitCells, cell);
                    zero |= !bit; one |= bit;
                    if (track.WeakBitMask.Length > (cell >> 3)) isWeak |= PackedBitCells.Get(track.WeakBitMask, cell);
                    if (zero && one && isWeak) break;
                }
                double a = one ? high : low, b = zero ? low : high;
                c.DrawLine(new Pen(isWeak ? weak : Brush("accent"), isWeak ? 2 : 1),
                    new Point(Left + x, a), new Point(Left + x, Math.Max(a + 1, b)));
            }
        }
        else
        {
            // Exact high-zoom bitcells, transitions and clock/data labels.
            long first = Math.Max(0, (long)Math.Floor(start));
            long last = Math.Min(track.BitCellCount, (long)Math.Ceiling(end));
            using (c.PushClip(new Rect(Left, 52, PlotWidth, Math.Max(1, Bounds.Height - 73))))
            for (long cell = first; cell < last; cell++)
            {
                bool bit = PackedBitCells.Get(track.PackedBitCells, (int)cell);
                double x = X(cell), y = bit ? high : low;
                double width = cellPixels;
                if (track.WeakBitMask.Length > (cell >> 3) && PackedBitCells.Get(track.WeakBitMask, (int)cell))
                    c.DrawRectangle(weak, null, new Rect(x, high, width, Math.Max(1, low - high)));
                c.DrawLine(signal, new Point(x, y), new Point(x + width, y));
                if (cell > 0 && PackedBitCells.Get(track.PackedBitCells, (int)cell - 1) != bit)
                    c.DrawLine(signal, new Point(x, high), new Point(x, low));
                if (cellPixels >= 14)
                    Label(c, $"{(stored ? "" : cell % 2 == 0 ? "C" : "D")}{(bit ? 1 : 0)}", x + 1, 59, 10);
            }
        }
        if (cellPixels * 16 >= 28 && (!stored || track.Encoding != PhysicalEncoding.Unknown))
        {
            foreach (var region in regions.Where(r => r.EndCell > start && r.StartCell < end && (!stored || r.Descriptor != null)))
            {
                long first = region.StartCell + (long)Math.Max(0, Math.Ceiling((start - region.StartCell) / 16)) * 16;
                for (long cell = first; cell + 16 <= Math.Min(Math.Min(end, track.BitCellCount), region.EndCell); cell += 16)
                    Label(c, ReadByte(track, cell).ToString("X2"), X(cell) + 2, 55, 11, Brushes.Black);
            }
        }
        var marker = new Pen(Brush("focus"), 1.5);
        foreach (long cell in track.IndexCells.Concat(track.Timing.Select(v => v.CellOffset)).Concat(track.V2Controls.Select(v => v.CellOffset))
            .Where(v => v >= start && v < end))
            c.DrawLine(marker, new Point(X(cell), 25), new Point(X(cell), Bounds.Height - 18));
        Label(c, stored ? "Stored cells · weak mask · index/timing markers · not measured flux"
            : cellPixels >= 5 ? "C = MFM clock · D = data · 16 cells / byte · synthetic signal"
            : "Gap / Sync / IDAM / CHRN / CRC / DAM / DATA · zoom for individual bits", 8, Bounds.Height - 16, 10);
    }
    private void SelectAt(Point point)
    {
        if (Session is not { TrackData: { } track } s || point.X < Left || point.X > Bounds.Width - Left) return;
        long cell = (long)Math.Clamp(Math.Floor(s.CellStart + (point.X - Left) / PlotWidth * s.CellLength), 0, track.BitCellCount - 1);
        var region = Regions(track).FirstOrDefault(r => cell >= r.StartCell && cell < r.EndCell);
        if (region?.Descriptor is int index) SectorSelected?.Invoke(new DskSectorAddress(s.TrackIndex, index));
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { if (e.Delta.Y == 0) return; Zoom(e.Delta.Y > 0 ? 1.5 : 1 / 1.5, Math.Clamp((e.GetPosition(this).X - Left) / PlotWidth, 0, 1)); e.Handled = true; }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; Focus(); press = e.GetPosition(this); startAtPress = Session?.CellStart ?? 0; moved = false; e.Pointer.Capture(this); e.Handled = true; }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (press is not { } p || Session is not { TrackData: { } track } s) return;
        double delta = e.GetPosition(this).X - p.X;
        if (Math.Abs(delta) > 3) moved = true;
        s.CellStart = Math.Clamp(startAtPress - delta / PlotWidth * s.CellLength, 0, Math.Max(0, track.BitCellCount - s.CellLength));
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { if (press is not null && !moved) SelectAt(e.GetPosition(this)); press = null; e.Pointer.Capture(null); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Add or Key.OemPlus) { Zoom(1.5); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { Zoom(1 / 1.5); e.Handled = true; }
        else if (e.Key == Key.Home) { Fit(); e.Handled = true; }
        else if (e.Key is Key.Left or Key.Right && Session is { TrackData: { } t } s)
        { s.CellStart = Math.Clamp(s.CellStart + (e.Key == Key.Left ? -1 : 1) * s.CellLength * .1, 0, Math.Max(0, t.BitCellCount - s.CellLength)); InvalidateVisual(); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
