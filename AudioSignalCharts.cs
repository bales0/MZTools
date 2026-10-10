using System;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MZTools;

// A single drawing surface per chart: no WPF element is allocated per PCM sample or pulse.
internal sealed class AudioSignalChart : FrameworkElement
{
    internal string Kind { get; init; } = "Waveform";
    internal AudioSignalAnalysis? Analysis { get; set; }
    internal WaveformRenderModel? Envelope { get; set; }
    internal AudioBlockAnalysis? Block { get; set; }
    internal AudioPulseDistribution? Distribution { get; set; }
    internal IReadOnlyList<AudioPulseDistribution>? HistogramSeries { get; set; }
    private AudioHistogramRange? histogramView;
    private AudioHistogramRange dragHistogram;
    private double? histogramZoomAnchor, histogramZoomPointer;
    internal long RenderedHistogramCount { get; private set; }
    internal AudioHistogramRange HistogramRange => histogramView ?? FullHistogramRange();
    internal string HistogramViewportLabel => histogramView == null ? "FULL RANGE" : "ZOOM / PAN — Fit histogram restores full range";
    internal AudioPulseReference? ReferenceOverlay { get; set; }
    internal bool ShowReferenceGuides { get; set; } = true;
    internal long? SelectionStart { get; private set; }
    internal long? SelectionEnd { get; private set; }
    internal int ChannelMode { get; set; } = 2;
    internal int RightShiftSamples { get; set; }
    internal bool InvertRight { get; set; }
    internal bool ShowIssueMarkers { get; set; }
    internal string SingleChannelLabel { get; set; } = "Mono";
    internal long? RangeStart { get; set; }
    internal long? RangeEnd { get; set; }
    internal event Action<AudioAnalysisIssue>? IssueSelected;
    internal int RenderedBucketCount { get; private set; }
    internal long Start { get; private set; }
    internal long End { get; private set; } = 1;
    internal event Action<long, long>? ViewChanged;
    internal event Action<AudioSignalRegion>? RegionSelected;
    internal event Action<long, long>? RangeSelected;
    internal event Action? SelectionChanged;
    private Point? dragStart; private long dragSample;
    private bool dragMoved; private long? selectionAnchor;
    private readonly List<(double X, string Text)> referenceHits = [];
    private readonly ToolTip hoverTip = new() { Placement = PlacementMode.Custom };
    private const double Left = 65, Bottom = 32, Right = 18;
    private double Top => Kind == "Waveform" ? 60 : 44;
    private double PlotWidth => Math.Max(1, ActualWidth - Left - Right);
    private double PlotHeight => Math.Max(1, ActualHeight - Top - Bottom);
    internal AudioSignalChart()
    {
        MinHeight = 170; Focusable = true; ClipToBounds = true;
        hoverTip.PlacementTarget = this;
        hoverTip.CustomPopupPlacementCallback = (size, _, _) =>
        {
            var p = Mouse.GetPosition(this);
            return new[] { new CustomPopupPlacement(new(p.X + 20, p.Y + 24), PopupPrimaryAxis.None),
                new CustomPopupPlacement(new(p.X - size.Width - 20, p.Y + 24), PopupPrimaryAxis.None),
                new CustomPopupPlacement(new(p.X + 20, p.Y - size.Height - 24), PopupPrimaryAxis.None),
                new CustomPopupPlacement(new(p.X - size.Width - 20, p.Y - size.Height - 24), PopupPrimaryAxis.None) };
        };
        ToolTip = hoverTip; ToolTipService.SetIsEnabled(this, false);
        MouseWheel += (_, e) =>
        {
            if (Kind != "Waveform" && Kind != "Histogram") return;
            ShowHover(null); double anchor = Math.Clamp((e.GetPosition(this).X - Left) / PlotWidth, 0, 1);
            if (Kind == "Histogram") ZoomHistogram(e.Delta > 0 ? .5 : 2, anchor); else Zoom(e.Delta > 0 ? .5 : 2, anchor);
            e.Handled = true;
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (Analysis == null) return;
            ShowHover(null);
            Focus(); Point p = e.GetPosition(this);
            if (Kind == "Waveform")
            {
                long sample = SampleAt(p.X);
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    selectionAnchor = sample; SetSelection(sample, Math.Min(Analysis.FrameCount, sample + 1)); CaptureMouse();
                }
                else if (ShowIssueMarkers && p.Y >= Top + 14 && p.Y < Top + 24)
                {
                    SelectIssueAt(sample);
                }
                else if (p.Y < Top + 18) SelectAtSample(sample);
                else { dragStart = p; dragSample = Start; dragMoved = false; CaptureMouse(); }
            }
            else if (Kind == "Histogram" && p.X >= Left && p.X <= Left + PlotWidth && p.Y >= Top)
            {
                dragStart = p; dragHistogram = HistogramRange; dragMoved = false; CaptureMouse();
            }
            else if (Kind == "Drift" && Block is { Drift.Count: > 0 })
            {
                double sample = Block.StartSample + Math.Clamp((p.X - Left) / PlotWidth, 0, 1) * (Block.EndSample - Block.StartSample);
                var w = Block.Drift.MinBy(w => Math.Abs((w.StartSample + w.EndSample) / 2.0 - sample))!; ViewChanged?.Invoke(w.StartSample, w.EndSample);
            }
        };
        MouseMove += (_, e) =>
        {
            Point p = e.GetPosition(this);
            if (selectionAnchor.HasValue && IsMouseCaptured) SetSelection(Math.Min(selectionAnchor.Value, SampleAt(p.X)), Math.Max(selectionAnchor.Value, SampleAt(p.X)) + 1);
            else if (dragStart.HasValue && IsMouseCaptured)
            {
                if (Math.Abs(p.X - dragStart.Value.X) >= 3) dragMoved = true;
                if (dragMoved && Kind == "Histogram")
                {
                    double delta = (p.X - dragStart.Value.X) / PlotWidth * (dragHistogram.Max - dragHistogram.Min);
                    if (delta != 0) ShowHistogramRange(dragHistogram.Min - delta, dragHistogram.Max - delta);
                }
                else if (dragMoved) SetRange(dragSample - (long)((p.X - dragStart.Value.X) / PlotWidth * (End - Start)), End - Start);
            }
            else if (Kind == "Waveform" && Analysis != null)
            {
                long sample = SampleAt(p.X);
                var region = p.Y < Top + 18 && p.X >= Left && p.X <= Left + PlotWidth
                    ? Analysis.Regions.Where(r => r.StartSample <= sample && r.EndSample > sample).OrderBy(r => r.EndSample - r.StartSample).FirstOrDefault() : null;
                string? name = region == null ? null : Analysis.Blocks.FirstOrDefault(b => b.Record == region.Record)?.Name;
                var issue = ShowIssueMarkers && p.Y >= Top + 14 && p.Y < Top + 24 ? IssueAt(sample) : null;
                ShowHover(issue != null ? $"{issue.Code}\n{issue.Description}\n{issue.StartSample / (double)Analysis.SampleRate:F6} … {issue.EndSample / (double)Analysis.SampleRate:F6} s" : region == null ? null : $"{(region.Record.HasValue ? $"{region.Record}: {name} / " : "")}{region.Kind}\n{region.StartSeconds:F3} … {region.EndSeconds:F3} s\n{region.Evidence}");
            }
            else if (Kind == "Histogram") ShowHover(p.Y >= Top + PlotHeight - 5
                ? string.Join("\n\n", referenceHits.Where(h => Math.Abs(h.X - p.X) < 7).Select(h => h.Text).Distinct()) : null);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (selectionAnchor.HasValue && SelectionStart.HasValue && SelectionEnd.HasValue) SelectAudioRange(SelectionStart.Value, SelectionEnd.Value);
            else if (Kind == "Waveform" && dragStart.HasValue && !dragMoved) SelectAtSample(SampleAt(e.GetPosition(this).X));
            selectionAnchor = null; dragStart = null; ReleaseMouseCapture();
        };
        LostMouseCapture += (_, _) => { dragStart = null; selectionAnchor = null; };
        MouseLeave += (_, _) => ShowHover(null);
    }
    private void ShowHover(string? text)
    {
        var pointer = Mouse.GetPosition(this);
        hoverTip.HorizontalOffset = pointer.X; hoverTip.VerticalOffset = pointer.Y;
        if (Equals(hoverTip.Content, text)) return;
        hoverTip.Content = text; ToolTipService.SetIsEnabled(this, !string.IsNullOrEmpty(text));
        if (string.IsNullOrEmpty(text)) hoverTip.IsOpen = false;
    }
    internal AudioSignalRegion? SelectAtSample(long sample)
    {
        var region = Analysis?.Regions.Where(r => r.StartSample <= sample && r.EndSample > sample).OrderBy(r => r.EndSample - r.StartSample).FirstOrDefault();
        if (region != null) { SetSelection(region.StartSample, region.EndSample); RegionSelected?.Invoke(region); }
        return region;
    }
    internal void SetSelection(long start, long end)
    {
        SelectionStart = Math.Clamp(start, 0, Analysis?.FrameCount ?? 0);
        SelectionEnd = Math.Clamp(end, SelectionStart.Value, Analysis?.FrameCount ?? 0);
        if (SelectionEnd <= SelectionStart) SelectionStart = SelectionEnd = null;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }
    internal void SelectAudioRange(long start, long end) { SetSelection(start, end); if (SelectionStart.HasValue && SelectionEnd.HasValue) RangeSelected?.Invoke(SelectionStart.Value, SelectionEnd.Value); }
    private long SampleAt(double x) => Start + (long)(Math.Clamp((x - Left) / PlotWidth, 0, 1) * (End - Start));
    private AudioAnalysisIssue? IssueAt(long sample)
    {
        long tolerance = Math.Max(1, (long)((End - Start) / PlotWidth * 3));
        return Analysis?.Issues.Where(i => i.StartSample <= sample + tolerance && i.EndSample > sample - tolerance)
            .OrderBy(i => i.StartSample <= sample && i.EndSample > sample ? 0 : Math.Min(Math.Abs(i.StartSample - sample), Math.Abs(i.EndSample - sample)))
            .ThenBy(i => i.EndSample - i.StartSample).FirstOrDefault();
    }
    internal AudioAnalysisIssue? SelectIssueAt(long sample)
    {
        var issue = IssueAt(sample);
        if (issue != null) { SetSelection(issue.StartSample, issue.EndSample); IssueSelected?.Invoke(issue); }
        return issue;
    }
    internal void Fit() { if (Analysis != null) SetRange(RangeStart ?? 0, Math.Max(1, (RangeEnd ?? Analysis.FrameCount) - (RangeStart ?? 0))); }
    internal void Zoom(double factor, double anchor = .5)
    {
        long count = Math.Max(8, (long)((End - Start) * factor));
        SetRange(Start + (long)((End - Start - count) * anchor), count);
    }
    internal void ShowRange(long start, long end) => SetRange(start, Math.Max(8, end - start));
    private void SetRange(long start, long count)
    {
        long first = RangeStart ?? 0, last = RangeEnd ?? Math.Max(1, Analysis?.FrameCount ?? 1);
        long frames = Math.Max(1, last - first); count = Math.Clamp(count, 1, frames);
        Start = Math.Clamp(start, first, last - count); End = Start + count;
        InvalidateVisual(); ViewChanged?.Invoke(Start, End);
    }
    private AudioHistogramRange FullHistogramRange()
    {
        var series = HistogramSeries ?? (Distribution == null ? Array.Empty<AudioPulseDistribution>() : new[] { Distribution });
        if (series.Count == 0) return new(0, 1);
        var groups = series.Select(d => d.Group).ToHashSet();
        var guides = !ShowReferenceGuides ? Array.Empty<AudioPulseGuide>() : ReferenceOverlay?.Guides.Where(g => groups.Contains(g.Group)).ToArray()
            ?? series.Where(d => d.Reference is > 0).Select(d => new AudioPulseGuide(d.Group, d.Reference!.Value, "Decoder reference")).ToArray();
        return AudioHistogramRange.Create(series[0] with { Histogram = series.SelectMany(d => d.Histogram).Concat(guides.Select(g => new AudioHistogramBin(g.Microseconds, 1))).ToArray(), Reference = null }, Analysis?.SampleRate ?? 0);
    }
    internal void FitHistogram() { histogramView = null; histogramZoomAnchor = histogramZoomPointer = null; InvalidateVisual(); }
    internal void ShowHistogramRange(double min, double max)
    {
        histogramZoomAnchor = histogramZoomPointer = null;
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min) throw new ArgumentException("Choose a finite, nonempty histogram interval in µs.");
        var bounds = FullHistogramRange();
        double minimumWidth = Math.Min(bounds.Max - bounds.Min, Math.Max(.01, 125000.0 / Math.Max(1, Analysis?.SampleRate ?? 0)));
        histogramView = bounds.Constrain(min, max, minimumWidth); InvalidateVisual();
    }
    internal void ZoomHistogram(double factor, double anchor = .5)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        var range = HistogramRange; double width = (range.Max - range.Min) * factor;
        anchor = Math.Clamp(anchor, 0, 1);
        // Clamping an edge must not select a new data anchor on the next wheel
        // event. Keep the original duration while the pointer remains in place.
        double duration = histogramZoomPointer.HasValue && Math.Abs(histogramZoomPointer.Value - anchor) < .00001
            ? histogramZoomAnchor!.Value : range.Min + (range.Max - range.Min) * anchor;
        double min = duration - width * anchor;
        ShowHistogramRange(min, min + width);
        histogramZoomAnchor = duration; histogramZoomPointer = anchor;
        if (dragStart.HasValue && IsMouseCaptured)
        {
            dragHistogram = HistogramRange; dragStart = Mouse.GetPosition(this);
        }
    }
    private void Text(DrawingContext dc, string text, double x, double y, Brush? brush = null, double size = 11) =>
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush ?? Brushes.DimGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    private void Line(DrawingContext dc, Brush color, double x1, double y1, double x2, double y2, double width = 1) => dc.DrawLine(new(color, width), new(x1, y1), new(x2, y2));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), new(0, 0, ActualWidth, ActualHeight));
        if (Analysis == null) { Text(dc, "Open a WAV or FLAC file to analyze.", Left, Top); return; }
        Line(dc, Brushes.Gray, Left, Top, Left, Top + PlotHeight); Line(dc, Brushes.Gray, Left, Top + PlotHeight, Left + PlotWidth, Top + PlotHeight);
        if (Kind == "Waveform") DrawWaveform(dc);
        else if (Block == null) Text(dc, "Select a decoded block.", Left + 10, Top + 10);
        else if (Kind == "Histogram") DrawHistogram(dc);
        else if (Kind == "Scatter") DrawScatter(dc);
        else DrawDrift(dc);
    }
    private void DrawWaveform(DrawingContext dc)
    {
        double X(long sample) => Left + (sample - Start) / (double)Math.Max(1, End - Start) * PlotWidth;
        var envelope = Envelope ?? Analysis!.Waveform;
        foreach (var region in Analysis!.Regions.Where(r => r.EndSample > Start && r.StartSample < End)
            .OrderBy(r => r.Kind == "Unassigned audio" ? 0 : r.Kind.StartsWith("Gap") ? 1 : r.Kind == "Unresolved" || r.Kind == "Recovered" ? 3 : 2))
        {
            Brush color = region.Kind == "Unassigned audio" || region.Kind.StartsWith("Gap") ? Brushes.LightGray : region.Kind == "Unresolved" ? Brushes.IndianRed : region.Kind == "Recovered" ? Brushes.Goldenrod : region.Kind.Contains("checksum") ? Brushes.MediumPurple : region.Kind.Contains("sync") ? Brushes.DarkOrange : Brushes.SteelBlue;
            double x = Math.Max(Left, X(region.StartSample)), w = Math.Max(1, Math.Min(Left + PlotWidth, X(region.EndSample)) - x);
            dc.DrawRectangle(color, null, new(x, Top, w, 13));
        }
        // One program name per record, above its first visible assigned region.
        // Gray unassigned audio never competes for label space.
        double labelEnd = Left;
        var labelRegions = Analysis.Regions.Where(r => r.Record.HasValue && r.EndSample > Start && r.StartSample < End &&
            r.Kind != "Recovered")
            .GroupBy(r => r.Record).Select(g => g.OrderBy(r => r.StartSample).First()).OrderBy(r => r.StartSample).ToArray();
        for (int i = 0; i < labelRegions.Length; i++)
        {
            var region = labelRegions[i];
            double x = Math.Max(Left, X(region.StartSample)) + 2;
            double next = i + 1 < labelRegions.Length ? Math.Max(Left, X(labelRegions[i + 1].StartSample)) : Left + PlotWidth;
            double width = Math.Max(1, next - x - 5);
            string name = Analysis.Blocks.FirstOrDefault(b => b.Record == region.Record)?.Name ?? region.Kind;
            var formatted = new FormattedText($"{region.Record}: {name}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
                Brushes.SteelBlue, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            if (x < labelEnd || x >= Left + PlotWidth) continue;
            dc.DrawText(formatted, new(x, Top - 17)); labelEnd = x + Math.Min(width, formatted.Width) + 3;
        }
        double mid = Top + 18 + (PlotHeight - 18) / 2, amplitude = Math.Max(1, (PlotHeight - 22) / 2);
        Line(dc, Brushes.LightGray, Left, mid, Left + PlotWidth, mid);
        var visible = envelope.VisibleBuckets(Start, End, Math.Max(1, (int)Math.Ceiling(PlotWidth)));
        RenderedBucketCount = visible.Count;
        void DrawChannel(bool right, Brush brush)
        {
            var channelBuckets = right && RightShiftSamples != 0 ? envelope.VisibleBuckets(Start - RightShiftSamples, End - RightShiftSamples, Math.Max(1, (int)Math.Ceiling(PlotWidth))) : visible;
            if (channelBuckets.Count == 0) return;
            bool samples = channelBuckets.All(b => b.EndSample - b.StartSample == 1);
            var geometry = new StreamGeometry();
            double Y(WaveformBucket b, bool upper) => mid - (right ? InvertRight ? upper ? -b.RightMin : -b.RightMax : upper ? b.RightMax : b.RightMin : upper ? b.LeftMax : b.LeftMin) * amplitude;
            double ClippedX(long sample) => Math.Clamp(X(sample + (right ? RightShiftSamples : 0)), Left, Left + PlotWidth);
            using (var context = geometry.Open())
            {
                context.BeginFigure(new(ClippedX(channelBuckets[0].StartSample), Y(channelBuckets[0], true)), !samples, !samples);
                foreach (var b in channelBuckets)
                {
                    context.LineTo(new(ClippedX(b.StartSample), Y(b, true)), true, false);
                    if (!samples) context.LineTo(new(ClippedX(b.EndSample), Y(b, true)), true, false);
                }
                if (!samples)
                    for (int i = channelBuckets.Count - 1; i >= 0; i--)
                    {
                        var b = channelBuckets[i]; context.LineTo(new(ClippedX(b.EndSample), Y(b, false)), true, false);
                        context.LineTo(new(ClippedX(b.StartSample), Y(b, false)), true, false);
                    }
            }
            geometry.Freeze(); dc.DrawGeometry(samples ? null : brush, samples ? new Pen(brush, 1) : null, geometry);
        }
        if (ChannelMode != 1) DrawChannel(false, Brushes.SteelBlue);
        if (ChannelMode != 0 && Analysis.ChannelCount > 1) DrawChannel(true, Brushes.DarkOrange);
        if (ShowIssueMarkers)
        {
            // Aggregate markers to screen pixels instead of drawing one object per pulse.
            foreach (var pixel in Analysis.Issues.Where(i => i.EndSample > Start && i.StartSample < End)
                .GroupBy(i => (X: Math.Clamp((int)X(Math.Max(Start, i.StartSample)), (int)Left, (int)(Left + PlotWidth)), Block: i.Code is "AUDIO_CHECKSUM_FAILURE" or "AUDIO_UNRESOLVED_REGION"))
                .OrderByDescending(g => g.Key.Block))
            {
                bool blockOnly = pixel.Key.Block;
                double finish = Math.Min(Left + PlotWidth, pixel.Max(i => X(i.EndSample)));
                dc.DrawRectangle(blockOnly ? Brushes.DarkOrange : Brushes.Crimson, null, new(pixel.Key.X, Top + 15, Math.Max(2, finish - pixel.Key.X), 6));
            }
        }
        if (SelectionStart.HasValue && SelectionEnd > SelectionStart && SelectionEnd > Start && SelectionStart < End)
        {
            double x = Math.Max(Left, X(SelectionStart.Value)), right = Math.Min(Left + PlotWidth, X(SelectionEnd.Value));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 20, 80, 190)), new Pen(Brushes.RoyalBlue, 1.5), new(x, Top, Math.Max(1, right - x), PlotHeight));
        }
        Text(dc, "+1 FS", 4, Top + 20); Text(dc, "0", 38, mid - 7); Text(dc, "−1 FS", 4, Top + PlotHeight - 15);
        AxisTime(dc, Start, End); Text(dc, $"PCM • {(Analysis.ChannelCount == 1 ? "blue " + SingleChannelLabel : "blue Left • orange Right")} • gray: unassigned • wheel: zoom • drag: pan • Shift+drag: select audio", Left, 3);
        if (ShowIssueMarkers) Text(dc, "Red: measured anomalies • orange: failed / unresolved block, exact damaged byte unknown", Left, 18, size: 10);
    }
    private void AxisTime(DrawingContext dc, long start, long end)
    {
        for (int i = 0; i <= 4; i++) { double x = Left + i * PlotWidth / 4; double seconds = (start + (end - start) * i / 4.0) / Analysis!.SampleRate; Text(dc, seconds.ToString("0.####") + " s", Math.Min(x, ActualWidth - 80), Top + PlotHeight + 5); }
    }
    private void DrawHistogram(DrawingContext dc)
    {
        RenderedHistogramCount = 0;
        referenceHits.Clear();
        var series = (HistogramSeries ?? (Distribution == null ? Array.Empty<AudioPulseDistribution>() : new[] { Distribution })).Where(d => d.Count > 0 && d.Histogram.Count > 0).ToArray();
        if (series.Length == 0) { Text(dc, "No pulses in the visible groups. Select a decoded block.", Left, Top + 20); return; }
        var visibleGroups = (HistogramSeries ?? series).Select(d => d.Group).ToHashSet();
        var guides = !ShowReferenceGuides ? Array.Empty<AudioPulseGuide>() : ReferenceOverlay?.Guides.Where(g => visibleGroups.Contains(g.Group)).ToArray()
            ?? series.Where(d => d.Reference is > 0).Select(d => new AudioPulseGuide(d.Group, d.Reference!.Value, "Decoder reference (unscaled)")).ToArray();
        var range = HistogramRange;
        double X(double value) => (value - range.Min) / (range.Max - range.Min) * PlotWidth;
        const int binWidth = 4;
        var pixels = series.SelectMany(d => d.Histogram.Select(b => (Distribution: d, Bin: b)))
            .Where(b => b.Bin.DurationMicroseconds >= range.Min && b.Bin.DurationMicroseconds <= range.Max)
            .GroupBy(b => Math.Clamp((int)X(b.Bin.DurationMicroseconds), 0, Math.Max(0, (int)PlotWidth - 1)) / binWidth * binWidth).ToArray();
        RenderedHistogramCount = pixels.Sum(g => g.Sum(b => b.Bin.Count));
        double maxCount = pixels.Length == 0 ? 1 : pixels.Max(g => g.Sum(b => b.Bin.Count));
        foreach (var pixel in pixels)
        {
            double y = Top + PlotHeight;
            foreach (var bin in pixel.GroupBy(b => b.Distribution.Group))
            {
                var d = bin.First().Distribution;
                double h = bin.Sum(b => b.Bin.Count) / maxCount * PlotHeight;
                bool outlier = d.Reference is > 0 && bin.Any(b => Math.Abs(b.Bin.DurationMicroseconds / (d.Reference.Value * Block!.ClassificationScale) - 1) > .35);
                y -= h;
                double width = Math.Min(binWidth - .5, PlotWidth - pixel.Key);
                dc.DrawRectangle(PulseBrush(d.Group), null, new(Left + pixel.Key, y, width, h));
                if (outlier) dc.DrawLine(new Pen(Brushes.Black, 1), new(Left + pixel.Key, y), new(Left + pixel.Key + width, y));
            }
        }
        if (guides.Length > 0) dc.DrawRectangle(Brushes.WhiteSmoke, null, new(Left, Top + PlotHeight + 1, PlotWidth, 8));
        foreach (var guide in guides.Where(g => g.Microseconds >= range.Min && g.Microseconds <= range.Max))
        {
            double x = Left + X(guide.Microseconds);
            int lane = Array.IndexOf(AudioPulseReference.Groups, guide.Group);
            // Four thin lanes preserve both colours when HIGH/LOW references coincide.
            dc.DrawRectangle(PulseBrush(guide.Group), null, new(Math.Clamp(x - 3, Left, Left + PlotWidth - 6), Top + PlotHeight + 1 + Math.Max(0, lane) * 2, 6, 2));
            dc.DrawLine(new Pen(PulseBrush(guide.Group), 1), new(x, Top + PlotHeight - 4), new(x, Top + PlotHeight));
            referenceHits.Add((x, $"{ReferenceOverlay?.Name ?? "Decoder reference"}\n{guide.Group}: {guide.Microseconds:F3} µs • {guide.Context}\n{ReferenceOverlay?.Evidence}\n{ReferenceOverlay?.Source}"));
        }
        Text(dc, $"{HistogramViewportLabel} • {range.Min:F1} … {range.Max:F1} µs • visible {RenderedHistogramCount:N0} / {series.Sum(d => d.Count):N0} pulses", Left, 2, histogramView == null ? Brushes.DimGray : Brushes.DarkOrange);
        Text(dc, "Blue SHORT High • red SHORT Low • green LONG High • orange LONG Low", Left, 17, size: 10);
        Text(dc, "Wheel: zoom • drag: pan • stacked counts / 4 px bin • reference strip on time axis • black cap: >35% residual", Left, 30, size: 10);
        for (int i = 0; i <= 4; i++)
            Text(dc, $"{range.Min + (range.Max - range.Min) * i / 4:F1} µs", Math.Min(Left + i * PlotWidth / 4, ActualWidth - 80), Top + PlotHeight + 12);
        Text(dc, maxCount.ToString("N0"), 3, Top);
    }
    internal static Brush PulseBrush(string group) => group switch { "SHORT High" => Brushes.Blue, "SHORT Low" => Brushes.Red, "LONG High" => Brushes.Green, _ => Brushes.DarkOrange };
    private void DrawScatter(DrawingContext dc)
    {
        var points = Block!.Scatter; if (points.Count == 0) return;
        double maxX = Math.Max(1, points.Max(p => p.HighMicroseconds)), maxY = Math.Max(1, points.Max(p => p.LowMicroseconds));
        foreach (var p in points)
            dc.DrawEllipse(p.Outlier ? Brushes.Crimson : p.Long ? Brushes.DarkOrange : Brushes.SteelBlue, null, new(Left + p.HighMicroseconds / maxX * PlotWidth, Top + PlotHeight - p.LowMicroseconds / maxY * PlotHeight), 1.5, 1.5);
        Text(dc, $"Blue SHORT • orange LONG • red outlier • shown {points.Count:N0}/{Block.ScatterPairs:N0} pairs", Left, 3);
        Text(dc, $"High: 0 … {maxX:F1} µs", Left, Top + PlotHeight + 5); Text(dc, $"Low µs\n{maxY:F0}", 2, Top);
    }
    private void DrawDrift(DrawingContext dc)
    {
        var windows = Block!.Drift; if (windows.Count == 0) return;
        double min = Math.Min(1, windows.Min(w => w.TimingScale)), max = Math.Max(1, windows.Max(w => w.TimingScale));
        double pad = Math.Max(.01, (max - min) * .1); min -= pad; max += pad;
        double Y(double v) => Top + (max - v) / (max - min) * PlotHeight;
        Line(dc, Brushes.DarkOrange, Left, Y(1), Left + PlotWidth, Y(1));
        Point? last = null;
        foreach (var w in windows)
        {
            var point = new Point(Left + ((w.StartSample + w.EndSample) / 2.0 - Block.StartSample) / Math.Max(1, Block.EndSample - Block.StartSample) * PlotWidth, Y(w.TimingScale));
            if (last.HasValue) dc.DrawLine(new Pen(Brushes.SteelBlue, 1), last.Value, point); last = point;
        }
        Text(dc, $"Scale • global {Block.GlobalTimingScale:F4} • speed {Block.SpeedOffsetPercent:+0.00;-0.00;0}% • orange: reference 1 • click: zoom window", Left, 3);
        Text(dc, max.ToString("F3"), 3, Top); Text(dc, min.ToString("F3"), 3, Top + PlotHeight - 12); AxisTime(dc, Block.StartSample, Block.EndSample);
    }
}
