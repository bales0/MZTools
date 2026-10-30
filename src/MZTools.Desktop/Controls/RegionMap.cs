using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Controls;

// Paints only the existing immutable Core layout. No media reads, decoding or writing.
public sealed class RegionMap : Control
{
    public static readonly StyledProperty<TapeSession?> SessionProperty = AvaloniaProperty.Register<RegionMap, TapeSession?>(nameof(Session));
    public TapeSession? Session { get => GetValue(SessionProperty); set => SetValue(SessionProperty, value); }
    public event Action<int, KeyModifiers>? FileSelected;
    private Point? press; private double startAtPress; private bool moved;
    public RegionMap() { Focusable = true; MinHeight = 130; ClipToBounds = true; }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
        {
            if (change.OldValue is TapeSession old) old.PropertyChanged -= SessionChanged;
            if (change.NewValue is TapeSession current) current.PropertyChanged += SessionChanged;
            InvalidateVisual();
        }
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
    private IBrush Brush(string key) => (IBrush)this.FindResource("F2." + key)!;
    private double Total => Session?.Snapshot?.Layout.Length ?? Session?.Model.Records.Count ?? 0;
    private double Length => Session is null || Session.MapLength <= 0 ? Math.Max(1, Total) : Math.Min(Math.Max(1, Total), Session.MapLength);
    private double Start => Math.Clamp(Session?.MapStart ?? 0, 0, Math.Max(0, Total - Length));
    public void Fit() { if (Session is null) return; Session.MapStart = 0; Session.MapLength = 0; InvalidateVisual(); }
    public void FitSelection()
    {
        if (Session is null) return;
        if (Session.SelectedRegion is { } region) { Session.MapStart = region.Start; Session.MapLength = Math.Max(1,region.Length); }
        else if (Session.Snapshot is { } snapshot)
        {
            var regions = snapshot.Layout.Regions.Where(r => r.FileIndex >= 0 && Session.Selection.Contains(r.FileIndex)).ToArray();
            if (regions.Length == 0) return;
            Session.MapStart = regions.Min(r => r.Start); Session.MapLength = Math.Max(1,regions.Max(r => r.End)-Session.MapStart);
        }
        else if (Session.SelectedRow is { } row) { Session.MapStart = row.Index; Session.MapLength = 1; }
        InvalidateVisual();
    }
    internal void Zoom(double factor, double anchor = .5)
    {
        if (Session is null || Total <= 0) return;
        double length = Math.Clamp(Length / factor, Math.Min(1, Total), Total);
        Session.MapStart = Math.Clamp(Start + Length * anchor - length * anchor, 0, Total - length);
        Session.MapLength = length; InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.DrawRectangle(Brush("surface"), new Pen(Brush("border")), new Rect(Bounds.Size));
        if (Session is null || Total == 0) { Text(context, "No mapped data", new(12,24)); return; }
        if (Session.Snapshot is { } snapshot)
        {
            foreach (var region in snapshot.Layout.Regions)
                DrawRegion(context, region.Start, region.Length, region.Name, region.Kind switch
                { QuickDiskRegionKind.Payload => "disk-file", QuickDiskRegionKind.Header => "disk-system", QuickDiskRegionKind.Gap => "disk-free", _ => "disk-unassigned" }, region.FileIndex);
        }
        else foreach (var row in Session.Model.Records.Select((r, i) => new RecordRow(r, i))) DrawRegion(context, row.Index, 1, row.Name, "disk-file", row.Index);
        Text(context, $"[{Start:N0}, {Start + Length:N0}) · {Session.Snapshot?.Layout.Unit ?? "record indices"}", new(12,96));
    }
    private void Text(DrawingContext c, string text, Point origin, IBrush? foreground = null) => c.DrawText(new FormattedText(text, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, Typeface.Default, 12, foreground ?? Brush("text")), origin);
    private void DrawRegion(DrawingContext c, double start, double length, string name, string color, int file)
    {
        double left = Math.Max(0, (start - Start) / Length * Bounds.Width), right = Math.Min(Bounds.Width, (start + length - Start) / Length * Bounds.Width);
        if (right <= left) return;
        var rect = new Rect(left, 24, Math.Max(1, right - left), 48);
        c.DrawRectangle(Brush(color), new Pen(Brush(file >= 0 && Session!.Selection.Contains(file) ? "focus" : "disk-border"), file >= 0 && Session!.Selection.Contains(file) ? 2 : 1), rect);
        if (right - left > 50) using (c.PushClip(rect)) Text(c, name, new(left + 5, 40), color is "disk-file" or "disk-system" ? Brush("on-accent") : Brush("text"));
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    { if(e.Delta.Y==0)return;Zoom(e.Delta.Y > 0 ? 1.5 : 1/1.5, Math.Clamp(e.GetPosition(this).X / Math.Max(1, Bounds.Width),0,1)); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Add or Key.OemPlus) { Zoom(1.5); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { Zoom(1/1.5); e.Handled = true; }
        else if (e.Key == Key.Home) { Fit(); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); press = e.GetPosition(this); startAtPress = Start; moved = false; e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (Session is null) return;
        Point position = e.GetPosition(this);
        if (press is { } original && (moved || Math.Abs(position.X-original.X) > 3))
        { moved = true; Session.MapStart = Math.Clamp(startAtPress - (position.X-original.X)/Math.Max(1,Bounds.Width)*Length, 0, Math.Max(0,Total-Length)); InvalidateVisual(); }
        if (Session.Snapshot is { } snapshot)
        {
            double point = Start + position.X / Math.Max(1,Bounds.Width) * Length;
            var region = snapshot.Layout.Regions.FirstOrDefault(r => point >= r.Start && point < r.End);
            ToolTip.SetTip(this, region is null ? null : snapshot.Layout.Detail(region));
        }
        base.OnPointerMoved(e);
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (press is null || Session is null) return;
        if (!moved)
        {
            double point = Start + e.GetPosition(this).X / Math.Max(1, Bounds.Width) * Length;
            if (Session.Snapshot is { } snapshot)
            {
                var region = snapshot.Layout.Regions.FirstOrDefault(r => point >= r.Start && point < r.End);
                if (region is not null) { Session.SelectRegion(region); if (region.FileIndex >= 0) FileSelected?.Invoke(region.FileIndex, e.KeyModifiers); }
            }
            else if (point >= 0 && point < Total) FileSelected?.Invoke((int)point,e.KeyModifiers);
        }
        press = null; e.Pointer.Capture(null); e.Handled = true;
    }
}
