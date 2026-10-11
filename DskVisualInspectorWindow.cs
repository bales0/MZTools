using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

internal sealed partial class DskVisualInspectorWindow : Window
{
    private DskVisualSnapshot snapshot;
    private readonly Func<int,DskSectorAddress?,bool> navigate;
    private readonly Action<DskSectorAddress> hex;
    private readonly Action<string> file;
    private readonly Func<IReadOnlyCollection<DskSectorAddress>,DskSectorAddress?,bool>? selectMany;
    private HashSet<DskSectorAddress> selectionKeys=new();
    private DskSectorAddress? selectionAnchor;
    private readonly WrapPanel cards=new();
    internal IReadOnlyCollection<DskSectorAddress> Selections=>selectionKeys;
    private readonly ComboBox trackChoice=new() { Width=230 }, sideChoice=new() { Width=100 }, widthChoice=new() { Width=180 }, layerChoice=new() { Width=155 };
    private readonly ComboBox severity=new() { Width=100 }, issueType=new() { Width=200 }, role=new() { Width=160 }, ownerFilter=new() { Width=190 };
    private readonly DataGrid tracks=Table(), sectors=Table(), issues=Table(), statistics=Table();
    private readonly ReportView details=new() {CompactHeader=true};
    private readonly TextBlock source=new() { TextWrapping=TextWrapping.Wrap }, status=new() { TextWrapping=TextWrapping.Wrap };
    private readonly DskVisualCanvas trackCanvas=new() { Kind=DskVisualCanvas.ViewKind.Track }, surfaceCanvas=new() { Kind=DskVisualCanvas.ViewKind.Surface };
    private readonly TabControl tabs=new();
    private readonly Button cancel=new() { Content="Cancel operation", IsEnabled=false };
    private CancellationTokenSource? work;
    private int revision;
    private bool updating, closed;
    private int selectedTrack;
    private DskSectorAddress? selected;
    private string[] relatedKeys=Array.Empty<string>();
    private DataGrid? physicalFiles;
    internal DskVisualSnapshot Snapshot => snapshot;
    internal int SelectedTrack => selectedTrack;
    internal DskSectorAddress? Selection => selected;
    internal void AddCommands(ContextMenu menu)
    {
        var button=new Button {Content="Disk / Tools ▾",Margin=new Thickness(4),ContextMenu=menu};
        button.Click+=(_,_)=> {menu.PlacementTarget=button;menu.IsOpen=true;};
        ((WrapPanel)((Grid)Content).Children[1]).Children.Add(button);
    }
    internal void AddFileList()
    {
        var files=Table();physicalFiles=files; Columns(files,("File","Name"),("CP/M user","User"),("Sectors","SectorCount"));
        files.ItemsSource=snapshot.Sectors.SelectMany(s=>s.Owners).DistinctBy(o=>o.FileKey).Select(o=>new {Key=o.FileKey,Name=o.FileName,User=o.User,SectorCount=snapshot.Sectors.Count(s=>s.Owners.Any(owner=>owner.FileKey==o.FileKey))}).ToArray();
        files.SelectionChanged+=(_,_)=> {
            if(updating || files.SelectedItem==null) return;
            var fileKeys=files.SelectedItems.Cast<object>().Select(row=>(string)row.GetType().GetProperty("Key")!.GetValue(row)!).ToHashSet();
            relatedKeys=fileKeys.ToArray(); var keys=snapshot.Sectors.Where(s=>s.Owners.Any(o=>fileKeys.Contains(o.FileKey))).Select(s=>s.Address).ToArray(); RequestMany(keys,keys.FirstOrDefault());
        };
        var menu=new ContextMenu();files.ContextMenu=menu;
        var export=new MenuItem {Header="Export file…"};menu.Items.Add(export);
        menu.Opened+=(_,_)=>export.IsEnabled=files.SelectedItems.Count==1 && snapshot.FileProjection!=null;
        export.Click+=(_,_)=> {
            if(files.SelectedItem==null || snapshot.FileProjection is not {} document) return;
            string key=(string)files.SelectedItem.GetType().GetProperty("Key")!.GetValue(files.SelectedItem)!;
            var entry=document.FileSystem.ReadDirectory().First(e=>e.Key==key);
            var save=new SaveFileDialog {Filter="Native file|*.*",FileName=entry.Name+(entry.Extension.Length==0?"":"."+entry.Extension)};
            if(save.ShowDialog(this)!=true) return;
            try {File.WriteAllBytes(save.FileName,document.FileSystem.Extract(entry));}
            catch(Exception ex) {MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Error);}
        };
        Tab("Files",files);
    }
    private void SelectOwnerFile(string key)
    {
        if(!snapshot.IsPhysical) {file(key);return;}
        var keys=snapshot.Sectors.Where(s=>s.Owners.Any(o=>o.FileKey==key)).Select(s=>s.Address).ToArray();
        relatedKeys=new[] {key};RequestMany(keys,keys.FirstOrDefault());tabs.SelectedIndex=5;
    }
    internal DskVisualInspectorWindow(Window parent,DskVisualSnapshot model,Func<int,DskSectorAddress?,bool> navigate,
        Action<DskSectorAddress> openHex,Action<string> openFile,Func<IReadOnlyCollection<DskSectorAddress>,DskSectorAddress?,bool>? selectMany=null,Action? rawSectors=null)
    {
        Owner=parent; snapshot=model; this.navigate=navigate; hex=openHex; file=openFile;
        this.selectMany=selectMany;
        trackChoice.DisplayMemberPath=nameof(DskVisualTrack.Label);
        Title="Visual "+model.SourceFormat+" Inspector — read-only";
        double availableWidth=Math.Max(320,SystemParameters.WorkArea.Width-24),availableHeight=Math.Max(240,SystemParameters.WorkArea.Height-24);
        Width=Math.Min(1250,availableWidth); Height=Math.Min(850,availableHeight); MinWidth=Math.Min(850,availableWidth); MinHeight=Math.Min(600,availableHeight);
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new Grid { Margin=new Thickness(10) }; Content=root;
        foreach(var height in new[] { GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto }) root.RowDefinitions.Add(new() { Height=height });
        root.Children.Add(source);
        var tools=new WrapPanel { Margin=new Thickness(0,8,0,8) }; Grid.SetRow(tools,1); root.Children.Add(tools);
        void Choice(string label,ComboBox box) { var pair=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,6,4)}; pair.Children.Add(new TextBlock { Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,4,0) }); pair.Children.Add(box); tools.Children.Add(pair); }
        Choice("Track",trackChoice); Choice("Side",sideChoice); Choice("Widths",widthChoice); Choice("Layer",layerChoice);
        widthChoice.ItemsSource=new[] { "Equal per descriptor", "Relative to stored bytes" }; widthChoice.SelectedIndex=0;
        layerChoice.ItemsSource=new[] { "Role","Ownership","Issue severity",model.IsPhysical?"CRC / deleted mark":"FDC status","Data length anomalies","Current selection" }; layerChoice.SelectedIndex=0;
        Button Tool(string label,Action action) { var b=new Button { Content=label,Margin=new Thickness(4,0,0,0),Padding=new Thickness(8,4,8,4) }; b.Click+=(_,_)=>action(); tools.Children.Add(b); return b; }
        Tool("Fit",()=>AllCanvases(c=>c.Fit())); Tool("Zoom +",()=>AllCanvases(c=>c.ZoomBy(1.25))); Tool("Zoom −",()=>AllCanvases(c=>c.ZoomBy(.8)));
        Tool("Export report…",async ()=>await ExportReport()); Tool("Copy summary",()=>Clipboard.SetText(Summary()));
        Grid.SetRow(tabs,2); root.Children.Add(tabs);
        Tab("Disk Surface",CreateSurfaceView());
        Tab("Track / Sector",SplitView(trackCanvas,sectors));
        var issueView=new DockPanel(); var filters=new WrapPanel { Margin=new Thickness(4) }; DockPanel.SetDock(filters,Dock.Top); issueView.Children.Add(filters);
        foreach(var pair in new[] { ("Severity",severity),("Type",issueType),("Role",role),("Owner",ownerFilter) }) {
            filters.Children.Add(new TextBlock { Text=pair.Item1,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5) }); filters.Children.Add(pair.Item2);
        }
        var jump=new Button { Content="Jump to issue",Margin=new Thickness(5) }; jump.Click+=(_,_)=>JumpIssue(); filters.Children.Add(jump);
        var issueDetails=new Button {Content="Finding details…",Margin=new Thickness(5)}; issueDetails.Click+=(_,_)=>ShowSelectionDetails(); filters.Children.Add(issueDetails);
        issueView.Children.Add(issues); Tab("Issues",issueView);
        Tab("Statistics",statistics); Tab("Tracks",tracks);
        cancel.Click+=(_,_)=>work?.Cancel();
        var footer=new DockPanel(); Grid.SetRow(footer,3); root.Children.Add(footer);
        DockPanel.SetDock(cancel,Dock.Right); footer.Children.Add(cancel); footer.Children.Add(status);
        Columns(tracks,("Track","PhysicalIndex"),("Slot C","SlotCylinder"),("Slot side","SlotSide"),("Declared C","DeclaredCylinder"),("Declared H","DeclaredSide"),("Missing","Missing"),("Sectors","SectorCount"),("Stored B","StoredBytes"),("Declared B","DeclaredBytes"),("Padding B","PaddingBytes"),("Issues","IssueCount"),("Descriptor R order","DescriptorSequence"));
        Columns(sectors,("Descriptor","Descriptor"),("C","C"),("H","H"),("R","R"),("N","N"),("Stored B","StoredBytes"),("Declared B","DeclaredBytes"),(model.IsPhysical?"Header CRC valid":"ST1",model.IsPhysical?"HeaderCrcValid":"St1"),(model.IsPhysical?"Data CRC valid":"ST2",model.IsPhysical?"DataCrcValid":"St2"),("Role","Role"),("Owner","Ownership"),("CP/M user","Users"),("Issues","IssueCount"),(model.IsPhysical?"Header cell":"Descriptor offset","DescriptorOffset"),(model.IsPhysical?"Data cell":"Data offset","DataOffset"));
        Columns(issues,("Severity","Severity"),("Code","Code"),("Location","Location"),("Description","Description"));
        Columns(statistics,("Category","Category"),("Value","Value"),("Meaning / provenance","Meaning"));
        ConfigureTextColumns(statistics,180,2,3);
        ConfigureTextColumns(issues,100,150,160,4);
        trackChoice.SelectionChanged+=(_,_)=> { if(!updating && trackChoice.SelectedItem is DskVisualTrack t) Request(t.PhysicalIndex,null,false); };
        sideChoice.SelectionChanged+=(_,_)=> { if(updating) return; ApplyFilters(); RefreshCanvases(); };
        widthChoice.SelectionChanged+=(_,_)=>RefreshCanvases(); layerChoice.SelectionChanged+=(_,_)=>RefreshCanvases();
        foreach(var f in new[] { severity,issueType,role,ownerFilter }) f.SelectionChanged+=(_,_)=> { if(!updating) ApplyFilters(); };
        sectors.SelectionChanged+=(_,e)=> { if(updating) return;
            var rows=sectors.SelectedItems.OfType<DskVisualSector>().ToArray();
            var keys=selectionKeys.Where(k=>k.Track!=selectedTrack).Concat(rows.Select(s=>s.Address)).ToHashSet();
            RequestMany(keys,e.AddedItems.OfType<DskVisualSector>().LastOrDefault()?.Address??rows.LastOrDefault()?.Address); };
        sectors.MouseDoubleClick+=(_,_)=>OpenHex();
        sectors.PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape) { RequestMany(Array.Empty<DskSectorAddress>(),null); e.Handled=true; } };
        tracks.MouseDoubleClick+=(_,_)=> { if(tracks.SelectedItem is DskVisualTrack t && Request(t.PhysicalIndex,null,false)) tabs.SelectedIndex=1; };
        issues.SelectionChanged+=(_,_)=> { if(!updating && issues.SelectedItem is DskAnalysisIssue i) details.Text=i.Explanation; };
        issues.MouseDoubleClick+=(_,_)=>JumpIssue();
        AllCanvases(c=>c.Activated+=(t,a,h)=>RequestGesture(t,a,h,Keyboard.Modifiers));
        InitializeFloatingAndMfm();
        foreach(var element in new FrameworkElement[] {surfaceCanvas,trackCanvas,sectors,mfmDetail}) {
            var menu=element.ContextMenu??new ContextMenu(); element.ContextMenu=menu;
            var item=new MenuItem {Header=model.IsPhysical && rawSectors==null?"Export Selected Sectors…":"Export / Import Selected Sectors…"}; menu.Items.Add(item);
            item.Click+=(_,_)=> {if(snapshot.IsPhysical && rawSectors==null) ExportPhysicalSelection();else rawSectors?.Invoke();};
            menu.Opened+=(_,_)=>item.IsEnabled=(rawSectors!=null || snapshot.IsPhysical) && selectionKeys.Count>0;
        }
        Closed+=(_,_)=> { closed=true; work?.Cancel(); detailWork?.Cancel(); generatedCache?.Dispose(); floatingDetails?.Close(); };
        SetSnapshot(model);
    }
    private void Tab(string name,UIElement content) => tabs.Items.Add(new TabItem { Header=name,Content=content });
    private void ExportPhysicalSelection()
    {
        var selectedSectors=snapshot.Sectors.Where(s=>selectionKeys.Contains(s.Address)).ToArray();
        if(selectedSectors.Length==0) return;
        var save=new SaveFileDialog {Filter="Decoded sector bytes|*.bin",FileName="selected-sectors.bin"};
        if(save.ShowDialog(this)!=true) return;
        try {File.WriteAllBytes(save.FileName,selectedSectors.SelectMany(s=>s.Data).ToArray());}
        catch(Exception ex) {MessageBox.Show(this,ex.Message,Title,MessageBoxButton.OK,MessageBoxImage.Error);}
    }
    private static Grid SplitView(FrameworkElement canvas,FrameworkElement table)
    {
        canvas.MinHeight=60;
        var grid=new Grid();
        grid.RowDefinitions.Add(new() {Height=new GridLength(1,GridUnitType.Star),MinHeight=60});
        grid.RowDefinitions.Add(new() {Height=new GridLength(6)});
        grid.RowDefinitions.Add(new() {Height=new GridLength(2,GridUnitType.Star),MinHeight=120});
        grid.Children.Add(canvas);
        var divider=new GridSplitter {Height=6,HorizontalAlignment=HorizontalAlignment.Stretch,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,Background=SystemColors.ControlDarkBrush,ToolTip="Drag to resize graph and table"};
        Grid.SetRow(divider,1); grid.Children.Add(divider); Grid.SetRow(table,2); grid.Children.Add(table);
        return grid;
    }
    private static void ConfigureTextColumns(DataGrid grid,params double[] widths)
    {
        for(int i=0;i<widths.Length;i++) {
            grid.Columns[i].Width=widths[i]<=4?new DataGridLength(widths[i],DataGridLengthUnitType.Star):new DataGridLength(widths[i]);
            var text=new Style(typeof(TextBlock)); text.Setters.Add(new Setter(TextBlock.TextWrappingProperty,TextWrapping.Wrap));
            text.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(3)));
            ((DataGridTextColumn)grid.Columns[i]).ElementStyle=text;
        }
    }
    private void ShowSelectionDetails()
    {
        var finding=tabs.SelectedIndex==2?issues.SelectedItem as DskAnalysisIssue:null;
        if(finding!=null) details.Text=finding.Explanation;
        else ShowDetails();
        OpenFloatingDetails();
    }
    private static DataGrid Table()
    {
        var g=new DataGrid { AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Extended,EnableRowVirtualization=true,EnableColumnVirtualization=true,ClipboardCopyMode=DataGridClipboardCopyMode.IncludeHeader };
        g.Resources[SystemColors.InactiveSelectionHighlightBrushKey]=SystemColors.HighlightBrush; g.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey]=SystemColors.HighlightTextBrush;
        return g;
    }
    private static void Columns(DataGrid grid,params (string Label,string Path)[] columns) {
        foreach(var c in columns) grid.Columns.Add(new DataGridTextColumn { Header=c.Label,Binding=new Binding(c.Path),Width=DataGridLength.Auto,MinWidth=65 });
    }
    private void AllCanvases(Action<DskVisualCanvas> action) { foreach(var c in new[] { trackCanvas,surfaceCanvas }) action(c); }
    private string Summary() => $"{snapshot.Cylinders} cylinders × {snapshot.Sides} sides; {snapshot.Sectors.Count()} sectors; filesystem {snapshot.FileSystem}; issues {snapshot.Issues.Count}; missing {snapshot.Tracks.Count(t=>t.Missing)} tracks";
    internal void SetVerificationDetails(string hash,bool canonicalPadding)
    {
        var rows=statistics.Items.Cast<object>().Where(row=> {
            string? category=row.GetType().GetProperty("Category")?.GetValue(row) as string;
            return category is not ("Current image SHA-256" or "Creator padding");
        }).ToList();
        rows.Add(new {Category="Current image SHA-256",Value=hash,Meaning="Current serialized image, including unsaved changes; not boot certification"});
        if(!snapshot.IsPhysical) rows.Add(new {Category="Creator padding",Value=canonicalPadding?"Canonical":"Non-canonical",Meaning="Creator-field trailing spaces/NULs; Normalize Container remains an explicit action"});
        statistics.ItemsSource=rows;
    }
    internal void SetSnapshot(DskVisualSnapshot model)
    {
        work?.Cancel(); detailWork?.Cancel(); generatedCache?.Dispose(); generatedCache=new(model); mfmDetail.SetTrack(null,0); revision++; snapshot=model; updating=true;
        try {
            trackChoice.ItemsSource=model.Tracks; tracks.ItemsSource=model.Tracks;
            sideChoice.ItemsSource=Enumerable.Range(0,model.Sides).Select(s=>$"Side {s}").Prepend("All sides").ToArray(); sideChoice.SelectedIndex=Math.Min(Math.Max(0,sideChoice.SelectedIndex),model.Sides);
            void Fill(ComboBox box,IEnumerable<string> values) { var old=box.SelectedItem as string; box.ItemsSource=values.Prepend("All").Distinct().ToArray(); box.SelectedItem=old??"All"; if(box.SelectedIndex<0) box.SelectedIndex=0; }
            Fill(severity,Enum.GetNames<DskIssueSeverity>()); Fill(issueType,model.Issues.Select(i=>i.Code)); Fill(role,Enum.GetNames<DskSectorRole>()); Fill(ownerFilter,model.Sectors.SelectMany(s=>s.Owners).Select(DskVisualSector.OwnerLabel).Prepend("Unassigned"));
            var rows=new List<object> {
                new { Category="Container",Value=Summary(),Meaning=model.OriginText },
                new { Category="Physical timing / CRC / flux",Value=model.TimingText,Meaning=model.IsPhysical?"CRC is recalculated from decoded fields; stored cell values are not measured flux.":"Stored ST1/ST2 flags do not prove current physical integrity." },
                new { Category="Trailing / padding",Value=$"{model.TrailingBytes} / {model.Tracks.Sum(t=>t.PaddingBytes)} B",Meaning="Stored container bytes" },
                new { Category="Filesystem mapping",Value=model.FileSystem,Meaning="Existing DskAnalyzer mapping; unknown ownership stays Unassigned. Candidate layouts retain their analyzer issues/confidence." }
            };
            foreach(var group in model.Issues.GroupBy(i=>i.Severity)) rows.Add(new { Category="Issues / "+group.Key,Value=group.Count().ToString(),Meaning="Existing DskAnalyzer issues" });
            foreach(var group in model.Sectors.GroupBy(s=>s.StoredBytes)) rows.Add(new { Category="Sector size",Value=$"{group.Key} B: {group.Count()}",Meaning="Stored byte distribution" });
            foreach(var group in model.Sectors.GroupBy(s=>s.Role)) rows.Add(new { Category="Role / "+group.Key,Value=group.Count().ToString(),Meaning="Existing filesystem mapping" });
            foreach(var group in model.Tracks.GroupBy(t=>t.SlotSide)) rows.Add(new { Category="Side "+group.Key,Value=$"{group.Count()} tracks; {group.Sum(t=>t.IssueCount)} track-associated issues",Meaning="Physical container slots; global filesystem issues remain listed separately" });
            var blocks=model.Sectors.SelectMany(s=>s.AllocationBlocks.Select(b=>(Block:b,Sector:s))).GroupBy(p=>p.Block).ToArray();
            rows.Add(new { Category="Mapped allocation blocks",Value=blocks.Length==0?"Unknown / not evaluated":$"{blocks.Length} distinct block IDs; {blocks.Count(g=>g.All(p=>p.Sector.Role.HasFlag(DskSectorRole.Free)))} marked free; {blocks.Count(g=>g.Any(p=>p.Sector.Owners.Count>0))} owned",Meaning="Only existing FS mapping; not an independent capacity estimate. Overlap findings come from analyzer Issues." });
            if(model.IsPhysical) foreach(var track in model.PhysicalImage!.Tracks) rows.Add(new {Category=$"C{track.Cylinder}/H{track.Side} cells",Value=$"{track.BitCellCount:N0}; weak {track.WeakBitMask.Sum(b=>System.Numerics.BitOperations.PopCount((uint)b))}; index {string.Join(", ",track.IndexCells)}",Meaning=$"Encoding {track.Encoding}; header bitrate {track.BitRate}; RPM {track.Rpm}; timing changes {track.Timing.Count}; v2 controls {track.V2Controls.Count}"});
            else foreach(var group in model.Sectors.GroupBy(s=>(s.St1,s.St2))) rows.Add(new { Category="Stored status",Value=$"ST1={group.Key.St1:X2} ST2={group.Key.St2:X2}: {group.Count()}",Meaning=DskVisualSnapshot.StatusFlags(group.Key.St1,group.Key.St2) });
            statistics.ItemsSource=rows;
            cards.Children.Clear();
            foreach(var card in new[] {($"{model.Sectors.Count():N0}","Sectors"),($"{model.Tracks.Count}","Tracks"),($"{model.Tracks.Count(t=>t.Missing)}","Missing"),($"{model.Issues.Count}","Analyzer findings")}) {
                var panel=new TextBlock {Text=card.Item1+" "+card.Item2,FontWeight=FontWeights.SemiBold};
                if(card.Item2=="Analyzer findings") {
                    var findingsButton=new Button {Content=panel,Padding=new Thickness(14,6,14,6),Margin=new Thickness(4),ToolTip="Show analyzer findings and their explanations"};
                    findingsButton.Click+=(_,_)=> { tabs.SelectedIndex=2; severity.SelectedIndex=issueType.SelectedIndex=role.SelectedIndex=ownerFilter.SelectedIndex=0; sideChoice.SelectedIndex=0; issues.Focus(); if(issues.Items.Count>0) { issues.SelectedIndex=0; issues.ScrollIntoView(issues.SelectedItem); } };
                    cards.Children.Add(findingsButton);
                } else cards.Children.Add(new Border {Child=panel,Padding=new Thickness(14,6,14,6),Margin=new Thickness(4),Background=SystemColors.ControlBrush,CornerRadius=new CornerRadius(5)});
            }
            AllCanvases(c=> { c.Snapshot=model; c.Generated=null; c.Refresh(); });
        } finally { updating=false; }
        source.Text="Read-only "+model.SourceFormat+" inspector · "+Summary();
        source.ToolTip=model.OriginText+"\n"+model.SurfaceText+"\nLegend: orange metadata; green data; gray unassigned/free; ! issues; blue selected; gold related files. Stored flags are not measured integrity.";
        SyncSelection(model.Tracks.Any(t=>t.PhysicalIndex==selectedTrack)?selectedTrack:0,selected,relatedKeys); ApplyFilters();
    }
    internal void SyncSelection(int track,DskSectorAddress? address,IEnumerable<string>? files=null,IEnumerable<DskSectorAddress>? selections=null)
    {
        if(closed) return;
        selectedTrack=track; selected=snapshot.Sectors.Any(s=>s.Address==address)?address:null;
        if(selections!=null) { var valid=snapshot.Sectors.Select(s=>s.Address).ToHashSet(); selectionKeys=selections.Where(valid.Contains).ToHashSet(); }
        else if(selected!=null && !selectionKeys.Contains(selected)) selectionKeys=new() {selected};
        if(files!=null) relatedKeys=files.ToArray();
        updating=true;
        try { trackChoice.SelectedItem=snapshot.Tracks.FirstOrDefault(t=>t.PhysicalIndex==track); tracks.SelectedItem=trackChoice.SelectedItem;
            sectors.ItemsSource=snapshot.Tracks.FirstOrDefault(t=>t.PhysicalIndex==track)?.Sectors;
            sectors.UnselectAll(); foreach(var s in sectors.Items.OfType<DskVisualSector>().Where(s=>selectionKeys.Contains(s.Address))) sectors.SelectedItems.Add(s);
            if(CurrentSector() is {} active) sectors.ScrollIntoView(active);
            if(physicalFiles!=null) {
                var owners=snapshot.Sectors.Where(s=>selectionKeys.Contains(s.Address)).SelectMany(s=>s.Owners).Select(o=>o.FileKey).ToHashSet();
                physicalFiles.UnselectAll();
                foreach(var row in physicalFiles.Items.Cast<object>()) if(owners.Contains((string)row.GetType().GetProperty("Key")!.GetValue(row)!)) physicalFiles.SelectedItems.Add(row);
            }
        } finally { updating=false; }
        updating=true;
        try { ApplyFilters(); } finally { updating=false; }
        if(mfmTrack!=selectedTrack) surfaceCanvas.Generated=null;
        ShowDetails(); RefreshCanvases();
    }
    internal bool Request(int track,DskSectorAddress? address,bool openHex)
    {
        if(!navigate(track,address)) { SyncSelection(selectedTrack,selected); return false; }
        SyncSelection(track,address,null,address==null?Array.Empty<DskSectorAddress>():new[] {address}); if(openHex) OpenHex(); return true;
    }
    internal bool RequestMany(IEnumerable<DskSectorAddress> keys,DskSectorAddress? focus)
    {
        var next=keys.ToHashSet(); focus=focus!=null && next.Contains(focus)?focus:next.FirstOrDefault();
        bool accepted=selectMany?.Invoke(next,focus)??navigate(focus?.Track??selectedTrack,focus);
        if(!accepted) { SyncSelection(selectedTrack,selected,null,selectionKeys); return false; }
        SyncSelection(focus?.Track??selectedTrack,focus,null,next); return true;
    }
    internal bool RequestGesture(int track,DskSectorAddress? address,bool openHex,ModifierKeys modifiers)
    {
        if(address==null) return Request(track,null,false);
        var order=snapshot.Tracks.Where(t=>tabs.SelectedIndex==0?t.SlotSide==track%Math.Max(1,snapshot.Sides):t.PhysicalIndex==track).SelectMany(t=>t.Sectors).Select(s=>s.Address).ToArray();
        var next=openHex && selectionKeys.Contains(address)?selectionKeys:DskSectorSelection.Next(selectionKeys,selectionAnchor,address,order,modifiers.HasFlag(ModifierKeys.Control),modifiers.HasFlag(ModifierKeys.Shift));
        bool result=RequestMany(next,address);
        if(result) { if(!modifiers.HasFlag(ModifierKeys.Shift)) selectionAnchor=address; if(openHex) OpenHex(); }
        return result;
    }
    private DskVisualSector? CurrentSector() => snapshot.Sectors.FirstOrDefault(s=>s.Address==selected);
    private void OpenHex() { if(selected!=null) hex(selected); }
    private void ShowDetails()
    {
        if(snapshot.PhysicalImage is {} physical) {
            var track=physical.Tracks[selectedTrack]; var sector=selected is {} a?track.Sectors.ElementAtOrDefault(a.Sector):null;
            details.Text=$"{physical.Variant} track {selectedTrack}; C{track.Cylinder}/H{track.Side}\nEncoding: {track.Encoding}; cells: {track.BitCellCount}; header bitrate: {track.BitRate}; RPM: {track.Rpm}\nIndex cells: {string.Join(", ",track.IndexCells)}\nWeak cells: {track.WeakBitMask.Sum(b=>System.Numerics.BitOperations.PopCount((uint)b))}\nTiming changes: {track.Timing.Count}; v2 controls: {track.V2Controls.Count}\nStored cells, not measured flux. Surface is schematic.\n";
            if(sector!=null) details.Text+=$"R{sector.R}; {sector.Data.Length} B; header cell {sector.HeaderCell}; data cell {sector.DataCell}\nHeader CRC: {sector.HeaderCrcValid}; data CRC: {sector.DataCrcValid}\nOwners: {CurrentSector()?.Ownership}; CP/M user: {CurrentSector()?.Users}\n";
            return;
        }
        var t=snapshot.Tracks.FirstOrDefault(t=>t.PhysicalIndex==selectedTrack); if(t==null) { details.Text="No track available."; return; }
        string text=$"{t.Label}; physical slot C{t.SlotCylinder}/H{t.SlotSide}\nBlock {t.BlockSize} B; padding {t.PaddingBytes} B; GAP3={t.Gap3}; filler={t.Filler}; stored rate/mode codes={t.DataRateCode}/{t.RecordingCode}\nInterleave pattern inferred from descriptor order: {t.DescriptorSequence}\n";
        var s=CurrentSector();
        if(s!=null) {
            text+=$"{s.Label}\nParsed N size: {s.ParsedNBytes?.ToString()??"Unknown"} B; stored {s.StoredBytes} B; declared {s.DeclaredBytes} B\nDescriptor offset 0x{s.DescriptorOffset:X}; data [0x{s.DataOffset:X}, 0x{s.EndOffset:X})\nST1=0x{s.St1:X2}; ST2=0x{s.St2:X2}: {s.Status}\nRole: {s.Role}; owners: {s.Ownership}\nLogical sectors: {string.Join(", ",s.LogicalBlocks)}; allocation blocks: {string.Join(", ",s.AllocationBlocks)}\n";
            text+="\nCP/M user: "+(string.IsNullOrEmpty(s.Users)?"Not applicable":s.Users)+"\n";
            text+=string.Join("\n",s.Owners.Select(o=>$"Owner: {DskVisualSector.OwnerLabel(o)}; extent={o.Extent}; logical sector {o.LogicalSector}; byte slice {o.Offset}..{o.Offset+o.Length}; file block {o.FileBlock}"));
            text+="\nHex preview (first 64 stored bytes): "+Convert.ToHexString(s.Data.AsSpan(0,Math.Min(64,s.Data.Length)))+"\n";
            text+=string.Join("\n",s.Issues.Select(i=>i.Explanation));
        } else text+=t.Missing?"Missing / unformatted track. No sector data.":$"{t.SectorCount} sector descriptors.\n"+string.Join("\n",t.Issues.Select(i=>i.Explanation));
        var selectedOwners=snapshot.Sectors.Where(sector=>selectionKeys.Contains(sector.Address)).SelectMany(sector=>sector.Owners).DistinctBy(owner=>owner.FileKey).ToArray();
        details.Text=$"Selected sectors: {selectionKeys.Count}\nSelected owners: {string.Join(", ",selectedOwners.Select(DskVisualSector.OwnerLabel))}\n"+text+"\nActual flux, physical CRC, rotation: "+snapshot.TimingText;
    }
    private void RefreshCanvases()
    {
        if(updating) return;
        int surfaceSide=sideChoice.SelectedIndex>0?sideChoice.SelectedIndex-1:-1;
        var related=snapshot.Sectors.Where(s=>s.Owners.Any(o=>relatedKeys.Contains(o.FileKey))).Select(s=>s.Address).ToHashSet();
        AllCanvases(c=> { c.TrackIndex=selectedTrack; c.Side=surfaceSide; c.Proportional=widthChoice.SelectedIndex==1; c.Layer=layerChoice.SelectedItem as string??"Role"; c.Selected=selected; c.Selection=selectionKeys; c.Related=related; c.InvalidateVisual(); });
        RefreshFloatingAndMfm();
    }
    private void ApplyFilters()
    {
        bool Match(DskVisualSector s) => (sideChoice.SelectedIndex<=0 || snapshot.Tracks.First(t=>t.PhysicalIndex==s.Track).SlotSide==sideChoice.SelectedIndex-1) &&
            (role.SelectedIndex<=0 || Enum.TryParse<DskSectorRole>(role.SelectedItem as string,out var r) && (r==DskSectorRole.Unknown?s.Role==r:s.Role.HasFlag(r))) &&
            (ownerFilter.SelectedIndex<=0 || ownerFilter.SelectedItem as string=="Unassigned" && s.Owners.Count==0 || s.Owners.Any(o=>DskVisualSector.OwnerLabel(o)==ownerFilter.SelectedItem as string));
        var byAddress=snapshot.Sectors.ToDictionary(s=>s.Address);
        var matched=byAddress.Values.Where(Match).Select(s=>s.Address).ToHashSet();
        if(severity.SelectedIndex>0 || issueType.SelectedIndex>0)
            matched.RemoveWhere(a=>!byAddress[a].Issues.Any(i=>
                (severity.SelectedIndex<=0 || i.Severity.ToString()==severity.SelectedItem as string) && (issueType.SelectedIndex<=0 || i.Code==issueType.SelectedItem as string)));
        issues.ItemsSource=snapshot.Issues.Where(i=>(severity.SelectedIndex<=0 || i.Severity.ToString()==severity.SelectedItem as string) &&
            (issueType.SelectedIndex<=0 || i.Code==issueType.SelectedItem as string) &&
            (sideChoice.SelectedIndex<=0 || i.Track is int t && snapshot.Tracks.Any(tr=>tr.PhysicalIndex==t && tr.SlotSide==sideChoice.SelectedIndex-1)) &&
            (role.SelectedIndex<=0 && ownerFilter.SelectedIndex<=0 || matched.Any(a=>a.Track==i.Track && (i.Sector==null || a.Sector==i.Sector)))).ToArray();
        AllCanvases(c=> { c.Filtered=matched; c.InvalidateVisual(); });
        if(sectors.ItemsSource!=null) CollectionViewSource.GetDefaultView(sectors.ItemsSource).Filter=item=>item is DskVisualSector s && matched.Contains(s.Address);
        status.Text=$"{matched.Count} matching sectors · {selectionKeys.Count} selected · {issues.Items.Count} filtered issues";
        status.ToolTip="Ctrl: toggle; Shift: range; Esc: clear. Drag: pan; wheel: zoom; Home: Fit; double-click sector: Hex. Use Floating details for properties; tables: Ctrl+C copies headers.";
    }
    private void JumpIssue() {
        if(issues.SelectedItem is not DskAnalysisIssue i) return;
        NavigateIssue(i);
    }
    private void NavigateIssue(DskAnalysisIssue i) {
        int? track=i.Track??snapshot.Sectors.FirstOrDefault(s=>i.FileKey!=null && s.Owners.Any(o=>o.FileKey==i.FileKey))?.Track;
        if(track is int t) {
            if(!Request(t,i.Sector is int d?new(t,d):null,false)) return;
            tabs.SelectedIndex=1; trackCanvas.Fit(); details.Text=i.Explanation+"\n\n"+details.Text;
        } else { details.Text=i.Explanation; OpenFloatingDetails(); }
    }
    private async Task ExportReport()
    {
        if(work!=null) return;
        var picker=new SaveFileDialog { Filter="JSON report|*.json|CSV sectors / issues / tracks|*.csv|Text report|*.txt",FileName=snapshot.SourceFormat+"-visual-analysis" };
        if(picker.ShowDialog(this)!=true) return;
        string format=picker.FilterIndex==1?"json":picker.FilterIndex==2?"csv":"txt"; var model=snapshot;
        var cancellation=new CancellationTokenSource(); work=cancellation; cancel.IsEnabled=true;
        string temporary=picker.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
        status.Text="Preparing diagnostic report…";
        try { string report=await Task.Run(()=>DskVisualReport.Export(model,format,cancellation.Token),cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); await File.WriteAllTextAsync(temporary,report,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); File.Move(temporary,picker.FileName,true);
            if(!closed) status.Text="Diagnostic report saved: "+picker.FileName;
        } catch(OperationCanceledException) { if(!closed) status.Text="Report export canceled."; }
        catch(Exception ex) { if(!closed) status.Text="Report export failed: "+ex.Message; }
        finally { if(File.Exists(temporary)) File.Delete(temporary); work=null; cancellation.Dispose(); cancel.IsEnabled=false; }
    }
    internal void ShowView(string view) {
        if(view=="Details") { ShowSelectionDetails(); return; }
        tabs.SelectedIndex=view switch { "Track"=>1,"Issues"=>2,"Statistics"=>3,"Tracks"=>4,_=>0 }; Activate();
    }
}
