using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MZTools;

internal sealed class DskRawRangeDialog : Window
{
    internal bool Applied { get; private set; }
    private readonly DataGrid list=new() {AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserSortColumns=false,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,EnableRowVirtualization=true,EnableColumnVirtualization=true};
    private readonly DskDiskMapControl map=new();
    private readonly TextBlock totals=new() {FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,8,0,8)};
    private readonly Button export=new() {Content="Export selected sectors…",Margin=new Thickness(4),Padding=new Thickness(12,6,12,6)};
    private readonly Button import=new() {Content="Import selected sectors…",Margin=new Thickness(4),Padding=new Thickness(12,6,12,6)};
    private DskSectorAddress? anchor;
    private bool updating;
    internal DskRawRangeDialog(Window owner, DskDocument document,IReadOnlyCollection<DskSectorAddress>? selection=null)
    {
        Owner = owner; Title = "Selected Sectors — Raw Export / Import"; Width = Math.Min(1050,SystemParameters.WorkArea.Width-24); Height = Math.Min(760,SystemParameters.WorkArea.Height-24);MinWidth=Math.Min(780,Width);MinHeight=Math.Min(600,Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock {Text="Sector transfer",FontSize=24,FontWeight=FontWeights.SemiBold});top.Children.Add(totals);
        top.Children.Add(new TextBlock { Text = "Select sectors in the map or table using Ctrl/Shift. Import must match the selected byte count exactly. Stored bytes are transferred without filesystem inversion.", TextWrapping = TextWrapping.Wrap });
        var controls=new WrapPanel {Margin=new Thickness(0,8,0,8)};top.Children.Add(controls);
        controls.Children.Add(new TextBlock {Text="Transfer order:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0)});
        var order = new ComboBox {MinWidth=230};order.Items.Add(new ComboBoxItem {Content="Physical descriptor order",Tag=DskRawOrder.Physical});order.Items.Add(new ComboBoxItem {Content="Sector ID order (within track)",Tag=DskRawOrder.SectorId});order.SelectedIndex=0;controls.Children.Add(order);
        var clear=new Button {Content="Clear selection",Margin=new Thickness(12,0,0,0)};clear.Click+=(_,_)=>list.UnselectAll();controls.Children.Add(clear);
        DskRawOrder SelectedOrder()=>(DskRawOrder)((ComboBoxItem)order.SelectedItem).Tag;
        var buttons = new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right}; DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);buttons.Children.Add(export);buttons.Children.Add(import);
        var close=new Button {Content="Close",IsCancel=true,Margin=new Thickness(4),Padding=new Thickness(12,6,12,6)};close.Click+=(_,_)=>Close();buttons.Children.Add(close);
        var body=new Grid();body.RowDefinitions.Add(new() {Height=new GridLength(2,GridUnitType.Star),MinHeight=100});body.RowDefinitions.Add(new() {Height=new GridLength(6)});body.RowDefinitions.Add(new() {Height=new GridLength(3,GridUnitType.Star),MinHeight=180});root.Children.Add(body);
        var mapGroup=new GroupBox {Header="Physical sector map · orange metadata · green data · blue selection"};body.Children.Add(mapGroup);
        mapGroup.Content=new ScrollViewer {Content=map,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var splitter=new GridSplitter {Height=6,HorizontalAlignment=HorizontalAlignment.Stretch,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=SystemColors.ControlDarkBrush};Grid.SetRow(splitter,1);body.Children.Add(splitter);
        Grid.SetRow(list,2);body.Children.Add(list);MapInteraction.EmphasizeSelection(list);
        void Column(string title,string path,double width=65)=>list.Columns.Add(new DataGridTextColumn {Header=title,Binding=new Binding(path),Width=width,MinWidth=45});
        Column("Track","Track");Column("C","C",45);Column("H","H",45);Column("#","PhysicalIndex",45);Column("R","R",45);Column("Bytes","DataLength",85);Column("Role","Role",160);Column("Files","Files",230);
        list.Columns[^1].Width=new DataGridLength(1,DataGridLengthUnitType.Star);list.Columns[^1].MinWidth=130;
        var layout=DskAnalyzer.Analyze(document);map.SetLayout(layout);list.ItemsSource=layout.Sectors.ToArray();
        void UpdateSelection() {
            var selected=list.SelectedItems.Cast<DskSectorLayout>().ToArray();var active=list.CurrentItem as DskSectorLayout??selected.FirstOrDefault();
            active=active!=null && selected.Contains(active)?active:selected.FirstOrDefault();
            map.SetSelection(selected.Select(s=>s.Address),active);if(active!=null) map.SelectSector(active);
            int tracks=selected.Select(s=>s.Track).Distinct().Count();
            totals.Text=$"{selected.Length:N0} {(selected.Length==1?"sector":"sectors")}   ·   {selected.Sum(s=>(long)s.DataLength):N0} bytes   ·   {tracks:N0} {(tracks==1?"track":"tracks")}";
            export.IsEnabled=selected.Length>0;import.IsEnabled=selected.Length>0 && !document.IsReadOnly;
            import.ToolTip=document.IsReadOnly?"This image is read-only.":"Preview exact-length raw import before applying changes.";
        }
        list.SelectionChanged+=(_,_)=> {if(!updating) {anchor=(list.CurrentItem as DskSectorLayout)?.Address;UpdateSelection();}};
        map.SelectionRequested+=(sector,modifiers)=> {
            var selected=sector==null?new HashSet<DskSectorAddress>():DskSectorSelection.Next(list.SelectedItems.Cast<DskSectorLayout>().Select(s=>s.Address),anchor,sector.Address,map.OrderedAddresses,modifiers.HasFlag(ModifierKeys.Control),modifiers.HasFlag(ModifierKeys.Shift));
            updating=true;try {list.UnselectAll();foreach(var row in list.Items.Cast<DskSectorLayout>().Where(s=>selected.Contains(s.Address))) list.SelectedItems.Add(row);
                if(sector!=null) {list.CurrentItem=sector;list.ScrollIntoView(sector);if(!modifiers.HasFlag(ModifierKeys.Shift)) anchor=sector.Address;}
            } finally {updating=false;}UpdateSelection();
        };
        order.SelectionChanged+=(_,_)=> {var view=CollectionViewSource.GetDefaultView(list.ItemsSource);using(view.DeferRefresh()) {view.SortDescriptions.Clear();view.SortDescriptions.Add(new("Track",ListSortDirection.Ascending));view.SortDescriptions.Add(new(SelectedOrder()==DskRawOrder.Physical?"PhysicalIndex":"R",ListSortDirection.Ascending));}if(list.SelectedItems.Count>0) list.ScrollIntoView(list.SelectedItems[0]);};
        if(selection!=null) foreach(var sector in list.Items.Cast<DskSectorLayout>().Where(s=>selection.Contains(s.Address))) list.SelectedItems.Add(sector);UpdateSelection();
        Loaded+=(_,_)=> {if(list.SelectedItems.Cast<DskSectorLayout>().FirstOrDefault() is {} first) {list.ScrollIntoView(first);map.SelectSector(first);}};
        PreviewKeyDown+=(_,e)=> {if(e.Key==Key.Escape && list.SelectedItems.Count>0) {list.UnselectAll();e.Handled=true;}};
        export.Click += (_, _) =>
        {
            try
            {
                var data = DskRawRangeService.Export(document, list.SelectedItems.Cast<DskSectorLayout>().Select(s => s.Address).ToArray(), SelectedOrder());
                var save = new SaveFileDialog { Filter = "Raw sectors|*.bin" }; if (save.ShowDialog(this) == true) File.WriteAllBytes(save.FileName, data);
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
        import.Click += (_, _) =>
        {
            try
            {
                var open = new OpenFileDialog { Filter = "Raw sectors|*.bin" }; if (open.ShowDialog(this) != true) return;
                var preview = DskRawRangeService.PreviewImport(document, list.SelectedItems.Cast<DskSectorLayout>().Select(s => s.Address).ToArray(), SelectedOrder(), File.ReadAllBytes(open.FileName));
                var dialog = new DskPatchPreviewWindow(this, preview.Report, () =>
                {
                    bool confirmed = !preview.RequiresFilesystemConfirmation || MessageBox.Show(this, "Accept all filesystem impact in the preview?", "Confirm raw import", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                    if (!confirmed) throw new InvalidOperationException("Import was cancelled.");
                    DskHexEditService.Apply(document, preview, confirmed);
                }, "Multi-sector Import Preview", "Apply import"); dialog.ShowDialog(); if (dialog.Applied) { Applied = true; Close(); }
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
    }
}
