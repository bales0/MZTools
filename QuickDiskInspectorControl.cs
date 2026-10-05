using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MZTools;

// Non-SHARP content is never presented as MZF records or passed to a SHARP writer.
internal sealed class QuickDiskInspectorControl : UserControl
{
    internal sealed record Unit(string Name, string Position, int Bytes, string Integrity, byte[] Data,
        string Structure = "", bool IsDecoded = false);
    private readonly DataGrid units = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
    internal QuickDiskInspectorControl(QdReadResult result, byte[] originalImage)
    {
        byte[] snapshot = (byte[])originalImage.Clone();
        string report = QuickDiskAnalysisReport.Build(result);
        var root = new DockPanel { Margin = new Thickness(8) }; Content = root;
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Button(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 4, 8, 4) };
            button.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(Window.GetWindow(this), e.Message, "QuickDisk inspection", MessageBoxButton.OK, MessageBoxImage.Error); } };
            buttons.Children.Add(button);
        }
        Button("Hex view selected unit", () =>
        {
            if (units.SelectedItem is not Unit unit) return;
            var browser = new HexBrowser { Owner = Window.GetWindow(this) };
            browser.ShowRawData(unit.Name, unit.Data); browser.Show();
        });
        Button("Export decoded block/sector...", () =>
        {
            if (units.SelectedItem is not Unit { IsDecoded: true } unit || unit.Data.Length == 0)
            {
                MessageBox.Show(Window.GetWindow(this), "Select a decoded host block or MO5 sector, not the raw container/track.", "Decoded data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var picker = new SaveFileDialog { Filter = "Decoded binary data|*.bin", FileName = "quickdisk-decoded.bin" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllBytes(picker.FileName, GetDecodedExport(unit));
        });
        if (result.Analysis.Content is Mo5QuickDiskContent mo5 && mo5.Sectors.Count == 400 && mo5.Sectors.All(s => s.HeaderValid && s.DataValid && !s.Duplicate))
            Button("Export MO5 logical raw image (51200 B)...", () =>
            {
                var picker = new SaveFileDialog { Filter = "MO5 logical raw data|*.bin", FileName = "mo5-raw.bin" };
                if (picker.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllBytes(picker.FileName, mo5.ExportRaw());
            });
        DockPanel.SetDock(buttons, Dock.Top); root.Children.Add(buttons);
        var details = new TextBox { Text = report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 220, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(details, Dock.Top); root.Children.Add(details);
        foreach (var (header, property) in new[] { ("Unit / physical & logical identity", "Name"), ("Position", "Position"), ("Decoded bytes", "Bytes"), ("Integrity", "Integrity") })
            units.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = property == "Name" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto });
        var items = new List<Unit>();
        if (result.PhysicalProfile is { } profile)
        {
            items.Add(new("Container header", "file 0x0", profile.Format == QdImageFormat.HxcPhysical ? 40 : 5, "Container validated", snapshot[..(profile.Format == QdImageFormat.HxcPhysical ? 40 : 5)]));
            items.Add(new("Track descriptor", $"file 0x{profile.DescriptorOffset:X}", 16, "Container validated", snapshot.AsSpan(profile.DescriptorOffset, 16).ToArray()));
            items.Add(new("Raw physical track — LSB-first bitcells", $"file 0x{profile.DataOffset:X}", profile.TrackLength, "Read-only", snapshot.AsSpan(profile.DataOffset, profile.TrackLength).ToArray()));
        }
        void AddBlock(QuickDiskHostBlock block) => items.Add(new(block.Description, $"cell {block.CellOffset} (0x{block.CellOffset:X})", block.Bytes.Length,
            block.IntegrityValid ? "CRC valid" : block.IsComplete ? "CRC invalid" : "Truncated", block.Bytes,
            QuickDiskUnitStructure.DescribeBlock(block, result), true));
        switch (result.Analysis.Content)
        {
            case RolandQuickDiskContent roland: foreach (var block in roland.Blocks) AddBlock(block); break;
            case AkaiQuickDiskContent akai: AddBlock(akai.Block); break;
            case Mo5QuickDiskContent sectors:
                foreach (var sector in sectors.Sectors) items.Add(new($"Physical #{sector.PhysicalSequence} / ID {sector.SectorId} / logical {sector.LogicalSector}", $"cell {sector.CellOffset}", sector.Data.Length,
                    $"Header {(sector.HeaderValid ? "valid" : "invalid")}; data {(sector.DataValid ? "valid" : "invalid/missing")}{(sector.Duplicate ? "; DUPLICATE" : "")}", sector.Data,
                    $"MO5 physical sequence: {sector.PhysicalSequence}\nOn-track ID: {sector.SectorId}\nLogical sector: {sector.LogicalSector}\nHeader: A5 / big-endian sector ID / additive checksum\nData: 5A / 128 payload bytes / additive checksum\nChecksums: header {(sector.HeaderValid ? "valid" : "invalid")}, data {(sector.DataValid ? "valid" : "invalid/missing")}\nDuplicate: {sector.Duplicate}\nPhysical range: cells {sector.CellOffset}..{sector.CellOffset + sector.CellLength}\nExport: decoded sector payload only ({sector.Data.Length} bytes), without sync/header/checksum.", true));
                break;
        }
        MapInteraction.EmphasizeSelection(units);
        units.PreviewKeyDown += (_, e) => { if (MapInteraction.MoveGridSelection(units, e.Key)) e.Handled = true; };
        var selectedDetail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 80 };
        units.SelectionChanged += (_, _) => selectedDetail.Text = units.SelectedItem is Unit unit
            ? $"{unit.Name}\nPosition: {unit.Position}; bytes: {unit.Bytes}; integrity: {unit.Integrity}\n{unit.Structure}" : "Select a decoded block/sector to inspect its structure.";
        var content = new Grid(); content.RowDefinitions.Add(new() { Height = new GridLength(2, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = new GridLength(6) }); content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(units);
        var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1); content.Children.Add(splitter); Grid.SetRow(selectedDetail, 2); content.Children.Add(selectedDetail);
        units.ItemsSource = items; root.Children.Add(content);
    }
    internal static byte[] GetDecodedExport(Unit unit)
    {
        if (!unit.IsDecoded || unit.Data.Length == 0) throw new InvalidOperationException("Select a non-empty decoded host block or sector.");
        return (byte[])unit.Data.Clone();
    }
}
