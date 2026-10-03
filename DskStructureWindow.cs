using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MZTools;

internal sealed class DskStructureWindow : Window
{
    private readonly DskStructureSnapshot snapshot;
    private readonly Action<DskStructureItem?>? selectInEditor;
    private readonly DskDiskMapControl map = new();
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button hex = new() { Content = "Hex view metadata...", IsEnabled = false, MinWidth = 130,
        ToolTip = "Read-only hex view of the selected native metadata. Filesystem-decoded bytes; storage inversion is removed where required." };
    private readonly Button clear = new() { Content = "Clear selection", Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly TabControl tabs = new();
    private readonly Dictionary<DskStructureTab, DataGrid> grids = new();
    private DskStructureItem? selected;
    private bool synchronizing;

    internal DskStructureWindow(Window? owner, DskStructureSnapshot snapshot, Action<DskStructureItem?>? selectInEditor = null)
    {
        Owner = owner; this.snapshot = snapshot; this.selectInEditor = selectInEditor;
        Title = "Filesystem Structure Inspector — read-only"; Width = 1100; Height = 820; MinWidth = 780; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(12) }; Content = root;
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(5), new GridLength(235), new GridLength(120), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        root.Children.Add(new TextBlock { Text = snapshot.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        foreach (var tab in Enum.GetValues<DskStructureTab>())
        {
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single, EnableRowVirtualization = true, ItemsSource = snapshot.Items.Where(i => i.Tab == tab).ToArray() };
            grid.Columns.Add(new DataGridTextColumn { Header = "Structure / location", Binding = new Binding(nameof(DskStructureItem.Name)), Width = 250 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Value / state / file", Binding = new Binding(nameof(DskStructureItem.Value)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.SelectionChanged += (_, _) => { if (!synchronizing) Select(grid.SelectedItem as DskStructureItem); };
            grids[tab] = grid; tabs.Items.Add(new TabItem { Header = tab == DskStructureTab.RawStructure ? "Raw structure" : tab.ToString(), Content = grid });
        }
        tabs.SelectionChanged += (_, e) =>
        {
            if (synchronizing || !ReferenceEquals(e.Source, tabs)) return;
            synchronizing = true; foreach (var grid in grids.Values) grid.SelectedItem = null; synchronizing = false; Select(null);
        };
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 2); root.Children.Add(splitter);
        map.SetLayout(snapshot.Layout);
        var scroll = new ScrollViewer { Content = map, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 3); root.Children.Add(scroll);
        map.SectorSelected += sector =>
        {
            if (sector == null) { ClearSelection(); return; }
            var item = snapshot.Items.FirstOrDefault(i => i.Tab == DskStructureTab.Allocation && i.Addresses.Contains(sector.Address));
            if (item == null) { ClearSelection(); map.SelectSector(sector); detail.Text = sector.Detail; return; }
            tabs.SelectedIndex = (int)item.Tab; grids[item.Tab].SelectedItem = item; grids[item.Tab].ScrollIntoView(item); Select(item);
        };
        detail.Margin = new Thickness(0, 8, 0, 0); Grid.SetRow(detail, 4); root.Children.Add(detail);
        var controls = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; controls.Children.Add(hex);
        clear.Click += (_, _) => ClearSelection();
        controls.Children.Add(clear);
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close(); controls.Children.Add(close); Grid.SetRow(controls, 5); root.Children.Add(controls);
        hex.Click += (_, _) =>
        {
            if (selected?.Raw == null) return;
            var browser = new HexBrowser { Owner = this }; browser.ShowRawData(selected.Name + " (filesystem-decoded snapshot)", (byte[])selected.Raw.Clone()); browser.ShowDialog();
        };
        Select(null);
    }

    internal void Select(DskStructureItem? item)
    {
        selected = item; hex.IsEnabled = item?.Raw != null; clear.IsEnabled = item != null;
        map.SelectSector(null); map.HighlightFiles(item?.Addresses.Select(a => $"{a.Track}:{a.Sector}") ?? []);
        var sector = item == null ? null : snapshot.Layout.Sectors.FirstOrDefault(s => item.Addresses.Contains(s.Address));
        map.SelectSector(sector);
        detail.Text = item == null ? "Select a directory slot, allocation block or native structure. Read-only; no DPB editor or automatic repair." :
            $"{item.Name}: {item.Value}\n{item.Detail}\n" + string.Join("; ", snapshot.Layout.Sectors.Where(s => item.Addresses.Contains(s.Address)).Select(s => $"T{s.Track}/index {s.PhysicalIndex}/R{s.R} @ image 0x{s.FileOffset:X}"));
        selectInEditor?.Invoke(item);
    }

    private void ClearSelection()
    {
        synchronizing = true; foreach (var grid in grids.Values) grid.SelectedItem = null; synchronizing = false; Select(null);
    }
}
