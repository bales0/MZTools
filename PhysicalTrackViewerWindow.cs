using Microsoft.Win32;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MZTools;

internal sealed class PhysicalTrackViewerWindow : Window
{
    internal PhysicalTrackViewerWindow(Window owner, HfeImage image, string? path = null)
    {
        Owner = owner; Title = $"{(image.IsV3 ? "HFEv3" : "HFE")} — Physical Track Viewer"; Width = 1000; Height = 750; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var tools = new WrapPanel(); DockPanel.SetDock(tools, Dock.Top); root.Children.Add(tools);
        var operations = new Button { Content = "Disk / Tools \u25BE", Margin = new Thickness(4, 0, 6, 0) };
        var commands = new ContextMenu(); operations.ContextMenu = commands; tools.Children.Add(operations);
        operations.Click += (_, _) => { commands.PlacementTarget = operations; commands.IsOpen = true; };
        var verify = new MenuItem { Header = "Verify Image..." }; commands.Items.Add(verify);
        verify.Click += (_, _) => new DskPatchPreviewWindow(this, ImageVerificationService.Verify(image.Serialize(image.IsV3)).Report, title: "Verify HFE — read-only").ShowDialog();
        var compare = new MenuItem { Header = "Compare with..." }; commands.Items.Add(compare);
        compare.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Disk images|*.hfe;*.dsk" }; if (picker.ShowDialog(this) != true) return;
            try { new DskPatchPreviewWindow(this, DskCompareService.CompareMedia(image.Serialize(image.IsV3), System.IO.File.ReadAllBytes(picker.FileName)), title: "Physical / decoded compare").ShowDialog(); }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
        var tracks = new ComboBox { ItemsSource = image.Tracks.Select(t => $"Track {t.Cylinder}, side {t.Side}").ToArray(), SelectedIndex = 0, Width = 180 }; tools.Children.Add(tracks);
        var inspectFilesystem = new MenuItem { Header = "Filesystem (read-only)..." }; commands.Items.Add(inspectFilesystem);
        inspectFilesystem.Click += (_, _) => new DskPatchPreviewWindow(this, HfeSemanticInspectionService.Inspect(image).Report, title: "HFE Filesystem Inspection — read-only").ShowDialog();
        foreach (var format in new[] { "HFEv3", "HFE", "DSK" })
        {
            var convert = new MenuItem { Header = $"Convert / Save As {format}..." }; commands.Items.Add(convert);
            convert.Click += (_, _) =>
            {
                try
                {
                    var preview = format == "DSK" ? MediaConversionService.HfeToDsk(image) : new MediaConversionPreview(image.Serialize(format == "HFEv3"), "Preserve supported physical cells, weak bits, timing and index positions.\nConversion refuses any physical property the selected HFE version cannot preserve. Source remains unchanged.", false);
                    var dialog = new DskPatchPreviewWindow(this, preview.Report, () =>
                    {
                        if (preview.IsLossy && MessageBox.Show(this, "Accept all physical information losses listed in the preview?", "Confirm lossy conversion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) throw new InvalidOperationException("Lossy conversion was not confirmed.");
                        var save = new SaveFileDialog { Filter = format == "DSK" ? "Extended DSK|*.dsk" : "HFE|*.hfe", FileName = "converted" };
                        if (save.ShowDialog(this) != true) throw new InvalidOperationException("Save was cancelled; no output was written.");
                        MediaConversionService.WriteVerified(save.FileName, preview.Output, bytes => { if (format == "DSK") DskImage.Parse(bytes); else HfeImage.Parse(bytes); }, path);
                    }, "Media Conversion Preview", "Convert / Save As..."); dialog.ShowDialog();
                }
                catch (Exception e) { MessageBox.Show(this, e.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
            };
        }
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 220, FontFamily = new FontFamily("Consolas") };
        DockPanel.SetDock(details, Dock.Bottom); root.Children.Add(details);
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) }; DockPanel.SetDock(summary, Dock.Top); root.Children.Add(summary);
        var map = new WrapPanel(); var mapScroll = new ScrollViewer { Content = map, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; DockPanel.SetDock(mapScroll, Dock.Top); root.Children.Add(mapScroll);
        var sectors = new ListBox(); root.Children.Add(sectors);
        PhysicalTrack? active = null;
        void Load()
        {
            active = image.Tracks[tracks.SelectedIndex];
            summary.Text = $"Cells: {active.BitCellCount}; bitrate: {active.BitRate?.ToString() ?? "unknown"} bit/s; RPM: {active.Rpm?.ToString() ?? "unknown"}; encoding: {active.Encoding} (container code: {active.ContainerEncoding?.ToString() ?? "synthetic"})\nSectors: {active.Sectors.Count}; order: {string.Join(",", active.Sectors.Select(s => s.R))}; CRC-error sectors: {active.Sectors.Count(s => !s.HeaderCrcValid || !s.DataCrcValid)}; weak cells: {Enumerable.Range(0, (int)active.BitCellCount).Count(i => active.WeakBitMask.Length > 0 && PackedBitCells.Get(active.WeakBitMask, i))}; timing changes: {active.Timing.Count}; index positions: {string.Join(",", active.IndexCells)}";
            sectors.ItemsSource = active.Sectors.Select((s, i) => $"#{i}: C/H/R/N={s.C}/{s.H}/{s.R}/{s.N}, {s.Data.Length} B, ID CRC={s.HeaderCrcValid}, data CRC={s.DataCrcValid}, IDAM cell={s.HeaderCell}, data cell={s.DataCell}").ToArray();
            map.Children.Clear(); details.Text = "Select a sector or map region. Physical map is ordered by cell offset.";
            void Region(string label, long offset, string detail)
            {
                var button = new Button { Content = label, Margin = new Thickness(1), ToolTip = $"Cell {offset}" }; map.Children.Add(button);
                button.Click += (_, _) =>
                {
                    int start = checked((int)Math.Clamp(offset, 0, active.BitCellCount));
                    int n = (int)Math.Min(256, active.BitCellCount - start);
                    details.Text = $"Cell offset: {offset}\n" + detail + "\nRaw bitcells (up to 256):\n" +
                        string.Concat(Enumerable.Range(start, n).Select(c => PackedBitCells.Get(active.PackedBitCells, c) ? '1' : '0'));
                };
            }
            long previous = 0;
            foreach (var s in active.Sectors)
            {
                Region("gap", previous, "Raw gap/undecoded cells; exact timing shown only when stored or derived from declared bitrate.");
                Region($"IDAM/header R={s.R}", s.HeaderCell, $"Address mark FE; C/H/R/N={s.C}/{s.H}/{s.R}/{s.N}; CRC valid: {s.HeaderCrcValid}");
                Region("DAM/data/CRC", s.DataCell, $"Data mark {s.DataMark:X2}; data CRC valid: {s.DataCrcValid}\n" + DskHexEditService.FormatHex(s.Data));
                previous = s.DataCell + (s.Data.Length + 2) * 16;
            }
            Region("gap/end", Math.Min(previous, active.BitCellCount), "Remaining raw cells.");
            foreach (var timing in active.Timing) Region("bitrate", timing.CellOffset, $"HFEv3 divisor {timing.Divisor}; declared bitrate {timing.BitRate:0.###} bit/s.");
            for (int cell = 0; cell < active.BitCellCount; cell++)
                if (active.WeakBitMask.Length > 0 && PackedBitCells.Get(active.WeakBitMask, cell))
                {
                    int begin = cell;
                    while (cell + 1 < active.BitCellCount && PackedBitCells.Get(active.WeakBitMask, cell + 1)) cell++;
                    Region("weak", begin, $"Weak cells: {cell - begin + 1}; range {begin}..{cell}. Random bits are represented by a deterministic zero inspection sample.");
                }
        }
        tracks.SelectionChanged += (_, _) => Load();
        sectors.SelectionChanged += (_, _) =>
        {
            if (active == null || sectors.SelectedIndex < 0) return;
            var s = active.Sectors[sectors.SelectedIndex];
            double? rate = active.Timing.LastOrDefault(t => t.CellOffset <= s.DataCell)?.BitRate ?? active.BitRate;
            details.Text = $"Header cell {s.HeaderCell}; data cell {s.DataCell}; declared local bitrate: {rate?.ToString("0.###") ?? "unknown"} bit/s\nTime is nominal from image bitrate, not measured flux timing.\nMarks: {s.AddressMark:X2}/{s.DataMark:X2}; CRC: {s.HeaderCrcValid}/{s.DataCrcValid}; weak data bits: {s.WeakBitMask.Sum(b => System.Numerics.BitOperations.PopCount((uint)b))}\n" + DskHexEditService.FormatHex(s.Data);
        };
        Load();
    }
}
