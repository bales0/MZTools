using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.IO;
using Microsoft.Win32;

namespace MZTools;

internal sealed class DskCompareWindow : Window
{
    private readonly DskComparison comparison;
    private readonly Action<DskDiffItem?>? selectInEditor;
    private readonly TabControl tabs = new();
    private readonly Dictionary<DskDiffLevel, DataGrid> grids = new();
    private readonly CheckBox showSame = new() { Content = "Show unchanged", Margin = new Thickness(12, 0, 0, 0) };
    private readonly DskDiskMapControl leftMap = new(), rightMap = new();
    private readonly ScrollViewer leftScroll = new(), rightScroll = new();
    private readonly Dictionary<ScrollViewer, (double X, double Y)> pendingScrolls = new();
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button hex = new() { Content = "Hex diff...", IsEnabled = false, MinWidth = 100 };
    private DskDiffItem? selected;
    private bool synchronizing;

    internal DskCompareWindow(Window? owner, DskComparison comparison, Action<DskDiffItem?>? selectInEditor = null)
    {
        Owner = owner; this.comparison = comparison; this.selectInEditor = selectInEditor;
        Title = "DSK Compare — snapshots / patch export"; Width = 1180; Height = 820; MinWidth = 800; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(12) }; Content = root;
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(5), new GridLength(240), new GridLength(115), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        var summary = new TextBlock { Text = $"Left: {comparison.LeftName}\nRight: {comparison.RightName}\n{comparison.Summary}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(summary); Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var divider = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(divider, 2); root.Children.Add(divider);
        foreach (var level in Enum.GetValues<DskDiffLevel>())
        {
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow, EnableRowVirtualization = true };
            grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(DskDiffItem.Status)), Width = 110 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Item", Binding = new Binding(nameof(DskDiffItem.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.SelectionChanged += (_, _) => { if (!synchronizing) Select(grid.SelectedItem as DskDiffItem); };
            grids.Add(level, grid);
            tabs.Items.Add(new TabItem { Header = level == DskDiffLevel.PhysicalSectors ? "Physical sectors" : level.ToString(), Content = grid });
        }
        tabs.SelectionChanged += (_, e) =>
        {
            if (!synchronizing && ReferenceEquals(e.Source, tabs))
            {
                synchronizing = true; foreach (var grid in grids.Values) grid.SelectedItem = null; synchronizing = false; Select(null);
            }
        };
        var maps = new Grid(); maps.ColumnDefinitions.Add(new ColumnDefinition()); maps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); maps.ColumnDefinitions.Add(new ColumnDefinition());
        void Map(DskDiskMapControl map, ScrollViewer scroll, string label, int col)
        {
            var panel = new DockPanel(); var caption = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(caption, Dock.Top); panel.Children.Add(caption);
            scroll.Content = map; scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto; scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            panel.Children.Add(scroll);
            Grid.SetColumn(panel, col); maps.Children.Add(panel);
        }
        leftMap.SetLayout(comparison.LeftLayout); rightMap.SetLayout(comparison.RightLayout);
        Map(leftMap, leftScroll, "Left snapshot — physical sector map", 0); Map(rightMap, rightScroll, "Right snapshot — physical sector map", 2);
        leftScroll.ScrollChanged += SyncMapScroll; rightScroll.ScrollChanged += SyncMapScroll;
        leftMap.SectorSelected += sector => SelectFromMap(sector, true); rightMap.SectorSelected += sector => SelectFromMap(sector, false);
        Grid.SetRow(maps, 3); root.Children.Add(maps);
        detail.Margin = new Thickness(0, 8, 0, 0); Grid.SetRow(detail, 4); root.Children.Add(detail);
        var controls = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; controls.Children.Add(hex); controls.Children.Add(showSame);
        var previewPatch = new Button { Content = "Preview patch", Margin = new Thickness(8, 0, 0, 0) };
        var exportPatch = new Button { Content = "Export patch...", Margin = new Thickness(8, 0, 0, 0) };
        controls.Children.Add(previewPatch); controls.Children.Add(exportPatch);
        previewPatch.Click += (_, _) => Patch(false);
        exportPatch.Click += (_, _) => Patch(true);
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(); controls.Children.Add(close); Grid.SetRow(controls, 5); root.Children.Add(controls);
        hex.Click += (_, _) => { if (selected?.HasHexDiff == true) new DskHexDiffWindow(this, selected).ShowDialog(); };
        showSame.Checked += (_, _) => RefreshRows(); showSame.Unchecked += (_, _) => RefreshRows(); RefreshRows();
    }

    private void Patch(bool export)
    {
        try
        {
            var patch = DskPatchService.Export(comparison.LeftImage, comparison.RightImage);
            if (!export)
            {
                var preview = DskPatchService.Preview(DskDocument.Open(comparison.LeftImage), patch);
                new DskPatchPreviewWindow(this, preview.Report).ShowDialog(); return;
            }
            var save = new SaveFileDialog { Filter = "MZTools sector patch|*.mzpatch.json", DefaultExt = ".mzpatch.json", AddExtension = true, FileName = "dsk-changes.mzpatch.json" };
            if (save.ShowDialog(this) == true) File.WriteAllText(save.FileName, DskPatchService.ToJson(patch));
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Sector patch export", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SyncMapScroll(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer source ||
            (e.HorizontalChange == 0 && e.VerticalChange == 0)) return;
        // ScrollTo offsets are applied by WPF during a later layout pass. Suppress the
        // resulting event, including clamped offsets on a smaller image, not only reentrancy.
        if (pendingScrolls.Remove(source, out var pending) &&
            Math.Abs(source.HorizontalOffset - pending.X) < 0.01 && Math.Abs(source.VerticalOffset - pending.Y) < 0.01) return;
        var target = ReferenceEquals(source, leftScroll) ? rightScroll : leftScroll;
        double x = Math.Clamp(source.HorizontalOffset, 0, target.ScrollableWidth);
        double y = Math.Clamp(source.VerticalOffset, 0, target.ScrollableHeight);
        if (Math.Abs(target.HorizontalOffset - x) < 0.01 && Math.Abs(target.VerticalOffset - y) < 0.01) return;
        pendingScrolls[target] = (x, y);
        target.ScrollToHorizontalOffset(x); target.ScrollToVerticalOffset(y);
    }

    private void RefreshRows()
    {
        synchronizing = true;
        foreach (var (level, grid) in grids) grid.ItemsSource = comparison.Items.Where(i => i.Level == level && (showSame.IsChecked == true || i.State != DskDiffState.Same)).ToArray();
        synchronizing = false; Select(null);
    }
    private void SelectFromMap(DskSectorLayout? sector, bool left)
    {
        if (sector == null) { foreach (var grid in grids.Values) grid.SelectedItem = null; Select(null); return; }
        var item = comparison.Items.FirstOrDefault(i => i.Level == DskDiffLevel.PhysicalSectors && (left ? i.LeftAddress : i.RightAddress) == sector.Address);
        if (item == null) return;
        if (item.State == DskDiffState.Same && showSame.IsChecked != true) showSame.IsChecked = true;
        tabs.SelectedIndex = (int)DskDiffLevel.PhysicalSectors;
        grids[DskDiffLevel.PhysicalSectors].SelectedItem = item; grids[DskDiffLevel.PhysicalSectors].ScrollIntoView(item);
    }
    internal void Select(DskDiffItem? item)
    {
        selected = item; hex.IsEnabled = item?.HasHexDiff == true; detail.Text = item?.Detail ?? "Select a row or a physical sector to inspect both snapshots. Export patch describes left → right; apply it from Disk Map.";
        void Highlight(DskDiskMapControl map, DskLayoutModel layout, DskSectorAddress? address, string? file)
        {
            map.SelectSector(file == null && address != null ? layout.Sectors.FirstOrDefault(s => s.Address == address) : null);
            map.HighlightFiles(file == null ? [] : [file]);
        }
        Highlight(leftMap, comparison.LeftLayout, item?.LeftAddress, item?.LeftFileKey);
        Highlight(rightMap, comparison.RightLayout, item?.RightAddress, item?.RightFileKey);
        selectInEditor?.Invoke(item);
    }
}

internal sealed class DskHexDiffWindow : Window
{
    private readonly ComboBox mode = new() { MinWidth = 260, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly DataGrid bytes = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, EnableRowVirtualization = true, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private readonly DskDiffItem item;

    internal DskHexDiffWindow(Window? owner, DskDiffItem item)
    {
        Owner = owner; this.item = item; Title = "Hex diff — " + item.Name; Width = 650; Height = 580; MinWidth = 450; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        DockPanel.SetDock(mode, Dock.Top); root.Children.Add(mode); DockPanel.SetDock(summary, Dock.Top); root.Children.Add(summary); root.Children.Add(bytes);
        foreach (var (label, property) in new[] { ("Relative offset", nameof(DskByteDifference.Address)), ("Left", nameof(DskByteDifference.LeftHex)), ("Right", nameof(DskByteDifference.RightHex)) })
            bytes.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        mode.ItemsSource = item.LeftStructure != null || item.RightStructure != null ? new[] { "Payload bytes", "Descriptor / raw directory bytes" } : new[] { "Selected raw bytes" };
        mode.SelectionChanged += (_, _) => Refresh();
        mode.SelectedIndex = (item.LeftStructure != null || item.RightStructure != null) && !DskCompareService.ByteDifferences(item.LeftBytes, item.RightBytes).Any() ? 1 : 0;
    }
    private void Refresh()
    {
        byte[]? left = mode.SelectedIndex == 1 ? item.LeftStructure : item.LeftBytes;
        byte[]? right = mode.SelectedIndex == 1 ? item.RightStructure : item.RightBytes;
        int count = DskCompareService.ByteDifferences(left, right).Count();
        bytes.ItemsSource = DskCompareService.ByteDifferences(left, right).Take(10000).ToArray();
        summary.Text = $"Left: {left?.Length ?? 0:N0} B | Right: {right?.Length ?? 0:N0} B | Differing offsets: {count:N0}\n" +
            "Offsets are relative to the selected representation, not image file offsets. -- means the byte is absent.\n" +
            (count > 10000 ? "Showing the first 10,000 differing offsets; all bytes were compared. Read-only." : "Read-only; only differing offsets are shown.");
    }
}
