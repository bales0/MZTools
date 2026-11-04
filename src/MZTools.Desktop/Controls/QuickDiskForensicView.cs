using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Controls;

// Embedded QuickDisk surface + unrolled MFM signal. All bytes and bitcells
// come from the authoritative Core QuickDiskVisualSnapshot. Not measured flux.
public sealed class QuickDiskForensicView : Control
{
    public TapeSession? Session { get; set; }
    private double start;
    private double length;
    private TapeSession? current;
    private Point? press;
    private double pressStart;
    private bool moved;
    private const double Side = 16;
    private double PlotWidth => Math.Max(1, Bounds.Width - 2 * Side);
    private IBrush Palette(string key) => this.FindResource("F2." + key) as IBrush ?? Brushes.SlateGray;
    private IBrush Fill(QuickDiskRegionKind kind) => kind switch
    {
        QuickDiskRegionKind.Header or QuickDiskRegionKind.Count => Palette("disk-directory"),
        QuickDiskRegionKind.Payload or QuickDiskRegionKind.HostBlock or QuickDiskRegionKind.HostSector => Palette("disk-file"),
        QuickDiskRegionKind.Framing => Palette("disk-unassigned"),
        QuickDiskRegionKind.Crc => Palette("disk-system"),
        QuickDiskRegionKind.OutsideWindow => Palette("readonly-bg"),
        _ => Palette("disk-free")
    };
    private void Label(DrawingContext dc, string text, double x, double y, double size = 11, IBrush? color = null)
        => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, color ?? Palette("text")), new Point(x, y));
    private QuickDiskVisualSnapshot? Snapshot => Session?.Snapshot;
    private double Total => Snapshot is { } s ? Math.Max(1, s.Layout.Length * (s.Layout.IsPhysical ? 1d : 16d)) : 1;
    public event Action? ViewportChanged;
    public double ViewportStart => start;
    public double ViewportLength => Math.Clamp(length, 1, Total);
    public double TotalCells => Total;
    public void ScrollTo(double cell)
    {
        double next = Math.Clamp(cell, 0, Math.Max(0, Total - ViewportLength));
        if (Math.Abs(next - start) < 0.25) return;
        start = next;
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }
    public QuickDiskForensicView() { Focusable = true; ClipToBounds = true; MinHeight = 310; }
    public void Bind(TapeSession? session)
    {
        if (current != session) { current = session; Session = session; Fit(); }
        else InvalidateVisual();
    }
    public void Fit() { start = 0; length = Total; InvalidateVisual(); ViewportChanged?.Invoke(); }
    public void FitSelected()
    {
        if (Snapshot is not { } snapshot || Session?.SelectedRegion is not { } r) return;
        double unit = snapshot.Layout.IsPhysical ? 1 : 16;
        double margin = Math.Max(16, r.Length * unit * .05);
        start = Math.Max(0, r.Start * unit - margin);
        length = Math.Clamp(r.Length * unit + 2 * margin, 16, Total);
        start = Math.Clamp(start, 0, Total - length); InvalidateVisual(); ViewportChanged?.Invoke();
    }
    public void Zoom(double factor, double anchor = .5)
    {
        if (Snapshot is null || !double.IsFinite(factor) || factor <= 0) return;
        double old = Math.Clamp(length, 1, Total);
        double next = Math.Clamp(old / factor, Math.Min(16, Total), Total);
        start = Math.Clamp(start + (old - next) * Math.Clamp(anchor, 0, 1), 0, Total - next);
        length = next; InvalidateVisual(); ViewportChanged?.Invoke();
    }
    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        dc.DrawRectangle(Palette("surface"), new Pen(Palette("border")), new Rect(Bounds.Size));
        if (Session is not { IsQuickDisk: true, Snapshot: { } snapshot })
        { Label(dc, "No QuickDisk image available", 12, 12); return; }
        if (snapshot.Layout.Length == 0) { Label(dc, "Empty QuickDisk image", 8, 12); return; }
        double unit = snapshot.Layout.IsPhysical ? 1d : 16d;
        double cx = Bounds.Width / 2, cy = 73;
        double r = 42;
        Label(dc, "QD disk surface (stored regions)", 8, 4, 12);
        if (Bounds.Width >= 145)
        {
            // Compact annular projection with true mapped-region positions.
            foreach (var region in snapshot.Layout.Regions)
            {
                var pen = new Pen(region == Session.SelectedRegion ? Palette("focus") : Fill(region.Kind), 13);
                double from = Math.Clamp(region.Start / (double)snapshot.Layout.Length, 0, 1);
                double to = Math.Clamp(region.End / (double)snapshot.Layout.Length, 0, 1);
                int steps = Math.Clamp((int)Math.Ceiling((to - from) * 360), 1, 360);
                for (int n = 0; n < steps; n++)
                {
                    double a = -Math.PI / 2 + 2 * Math.PI * (from + (to - from) * n / steps);
                    double b = -Math.PI / 2 + 2 * Math.PI * (from + (to - from) * (n + 1) / steps);
                    dc.DrawLine(pen, new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a)),
                        new Point(cx + r * Math.Cos(b), cy + r * Math.Sin(b)));
                }
            }
            dc.DrawEllipse(Palette("surface"), new Pen(Palette("border")), new Point(cx, cy), 18, 18);
        }
        Label(dc, $"{snapshot.Layout.Length:N0} {snapshot.Layout.Unit} · {(snapshot.Layout.IsPreview ? "rebuilt preview" : "original image")}", 8, 124, 10);
        double top = 166;
        double high = 239;
        double low = Math.Max(259, Bounds.Height - 32);
        double actualLength = Math.Clamp(length, 1, Total);
        double cellPixels = PlotWidth / actualLength;
        double X(double cell) => Side + (cell - start) * cellPixels;
        Label(dc, $"Unrolled MFM / bitcells [{(long)start:N0}, {(long)(start + actualLength):N0})", 8, 146, 11);
        // Draw exact region colors independently of display resolution.
        foreach (var region in snapshot.Layout.Regions.Where(v => (v.End * unit) > start && v.Start * unit < start + actualLength))
        {
            double a = Math.Max(Side, X(Math.Max(start, region.Start * unit)));
            double b = Math.Min(Bounds.Width - Side, X(Math.Min(start + actualLength, region.End * unit)));
            if (b <= a) continue;
            dc.DrawRectangle(Fill(region.Kind), null, new Rect(a, top, b - a, 8));
        }
        // HEX bytes appear when individual decoded bytes are wide enough.
        if (cellPixels * 16 >= 30)
        {
            foreach (var region in snapshot.Layout.Regions.Where(r => r.End * unit > start && r.Start * unit < start + actualLength &&
                r.Kind is not (QuickDiskRegionKind.Gap or QuickDiskRegionKind.OutsideWindow)))
            {
                long byteStart = snapshot.ByteStart(region);
                double logicalUnit = snapshot.Layout.IsPhysical ? 16 : 1;
                long visibleStart = (long)Math.Floor(start / unit);
                long visibleEnd = (long)Math.Ceiling((start + actualLength) / unit);
                int first = (int)Math.Max(0, Math.Ceiling((visibleStart - byteStart) / logicalUnit));
                int last = (int)Math.Min((region.End - byteStart) / logicalUnit, Math.Ceiling((visibleEnd - byteStart) / logicalUnit));
                if (snapshot.Layout.HostBytes.TryGetValue(region, out var host)) last = Math.Min(last, host.Length);
                for (int i = first; i < last; i++)
                {
                    long position = byteStart + i * (long)logicalUnit;
                    if (position < region.Start || position >= region.End) continue;
                    Label(dc, snapshot.Byte(region, i).ToString("X2"), X(position * unit) + 2, top + 11, 10);
                }
            }
        }
        if (cellPixels < 3)
        {
            int pixels = Math.Max(1, (int)PlotWidth);
            for (int p = 0; p < pixels; p++)
            {
                long first = (long)Math.Floor(start + actualLength * p / pixels);
                long last = (long)Math.Ceiling(start + actualLength * (p + 1) / pixels);
                // Full min/max reading is intentionally bounded per pixel in overview;
                // do not confuse this with a reconstructed exact transition trace.
                int samples = 0; bool zero = false, one = false;
                long stride = Math.Max(1, (last - first + 255) / 256);
                for (long bit = first; bit < last && samples++ < 256; bit += stride)
                {
                    bool value = snapshot.SignalBit(Math.Clamp(bit, 0, (long)Total - 1)) != 0;
                    zero |= !value; one |= value;
                    if (zero && one) break;
                }
                double a = one ? high : low, b = zero ? low : high;
                dc.DrawLine(new Pen(Palette("accent")), new Point(Side + p, a), new Point(Side + p, Math.Max(a + 1, b)));
            }
            Label(dc, "Overview: sampled envelope; zoom for exact bitcells", 8, Bounds.Height - 17, 10);
        }
        else
        {
            using (dc.PushClip(new Rect(Side, top + 28, PlotWidth, Math.Max(1, Bounds.Height - top - 45))))
            for (long bit = Math.Max(0, (long)Math.Floor(start)); bit < Math.Min((long)Total, (long)Math.Ceiling(start + actualLength)); bit++)
            {
                int b = snapshot.SignalBit(bit);
                double x = X(bit), y = b != 0 ? high : low;
                var line = new Pen(Palette("accent"), 1.25);
                dc.DrawLine(line, new Point(x, y), new Point(x + cellPixels, y));
                if (bit > 0 && snapshot.SignalBit(bit - 1) != b)
                    dc.DrawLine(line, new Point(x, high), new Point(x, low));
                if (cellPixels >= 14)
                    Label(dc, $"{(bit % 2 == 0 ? "C" : "D")}{b}", x + 1, top + 30, 10);
            }
            Label(dc, snapshot.Layout.IsPhysical ? "Stored QD cells · not flux timing" : "Synthetic MFM from logical bytes", 8, Bounds.Height - 17, 10);
        }
    }
    private void SelectAt(Point p)
    {
        if (Snapshot is not { } snap || Session is null) return;
        long position;
        if (p.Y < 127)
        {
            double angle = Math.Atan2(p.Y - 73, p.X - Bounds.Width / 2) + Math.PI / 2;
            if (angle < 0) angle += 2 * Math.PI;
            position = (long)(angle / (2 * Math.PI) * snap.Layout.Length);
        }
        else
        {
            position = (long)Math.Floor((start + (p.X - Side) / PlotWidth * length) /
                (snap.Layout.IsPhysical ? 1d : 16d));
        }
        var region = snap.Layout.Regions.FirstOrDefault(v => position >= v.Start && position < v.End);
        if (region is null) return;
        Session.SelectRegion(region);
        InvalidateVisual();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { if (e.Delta.Y == 0) return; Zoom(e.Delta.Y > 0 ? 1.5 : 1 / 1.5, Math.Clamp((e.GetPosition(this).X - Side) / PlotWidth, 0, 1)); e.Handled = true; }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; Focus(); press = e.GetPosition(this); pressStart = start; moved = false; e.Pointer.Capture(this); e.Handled = true; }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (press is not { } p || e.GetPosition(this).Y < 140) return;
        var delta = e.GetPosition(this).X - p.X;
        if (Math.Abs(delta) > 3) moved = true;
        start = Math.Clamp(pressStart - delta / PlotWidth * length, 0, Math.Max(0, Total - length));
        InvalidateVisual(); ViewportChanged?.Invoke();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    { if (press is not null && !moved) SelectAt(e.GetPosition(this)); press = null; e.Pointer.Capture(null); }
    protected override void OnKeyDown(KeyEventArgs e)
    { if (e.Key is Key.Add or Key.OemPlus) { Zoom(1.5); e.Handled = true; } else if (e.Key is Key.Subtract or Key.OemMinus) { Zoom(1 / 1.5); e.Handled = true; } else if (e.Key == Key.Home) { Fit(); e.Handled = true; } base.OnKeyDown(e); }
}
