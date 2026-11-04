using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class DskVisualParityTests
{
    private static T Named<T>(Control root,string name)where T:Control=>root.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
    private static void Pump(Window window){Dispatcher.UIThread.RunJobs();window.UpdateLayout();}
    private static void Capture(Window window,string name)
    {
        string path=Environment.GetEnvironmentVariable("MZTOOLS_DSK_PARITY_OUTPUT")??Path.Combine(Path.GetTempPath(),"mztools-dsk-parity");Directory.CreateDirectory(path);Pump(window);
        using var image=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96));image.Render(window);image.Save(Path.Combine(path,name),PngBitmapEncoderOptions.Default);
    }
    private static DskDocument Fixture()
    {var disk=DskDocumentFactory.CreateFsmz(false);disk.FileSystem.Insert("FIRST",[1,2,3],1,0x4000,0x4000);disk.MarkModified();return disk;}
    [AvaloniaFact]public async Task SurfaceControlsTabsAndAllLayersMatchWpfAndKeepBytes()
    {
        using var files=new DiskDocumentTests();var s=await files.Open(Fixture());byte[] before=s.SerializeContainer();var window=new DiskVisualWindow(files.Shell);window.Show();
        try
        {
            Pump(window);Assert.Same(s.Snapshot,window.Snapshot);
            Assert.Equal(new[]{"Disk Surface","Track / Sector","Issues","Statistics","Tracks"},Named<TabControl>(window,"DskVisualTabs").Items.OfType<TabItem>().Select(t=>t.Header));
            Assert.Equal(new[]{"Role","Ownership","Issue severity","FDC status","Data length anomalies","Current selection"},Named<ComboBox>(window,"VisualLayerChoice").Items.Cast<string>());
            var graph=Named<DskVisualCanvas>(window,"VisualSurfaceCanvas");Assert.Equal(-1,graph.Side);Assert.False(graph.Proportional);
            Named<ComboBox>(window,"VisualWidthChoice").SelectedIndex=1;Assert.True(graph.Proportional);
            foreach(string layer in Named<ComboBox>(window,"VisualLayerChoice").Items.Cast<string>().ToArray()){Named<ComboBox>(window,"VisualLayerChoice").SelectedItem=layer;Assert.Equal(layer,graph.Layer);Capture(window,"avalonia-dsk-layer-"+layer.Replace(' ','-')+".png");}
            Named<ComboBox>(window,"VisualLayerChoice").SelectedIndex=0;Named<ComboBox>(window,"VisualWidthChoice").SelectedIndex=0;Capture(window,"avalonia-dsk-surface.png");
            var sides=Named<ComboBox>(window,"VisualSideChoice");sides.SelectedIndex=2;Assert.Equal(1,graph.Side);
            var track=s.Snapshot.Tracks.Last();Named<ComboBox>(window,"VisualTrackChoice").SelectedItem=track;Assert.Equal(track.PhysicalIndex,window.SelectedTrack);
            Assert.Equal(before,s.SerializeContainer());Assert.False(s.IsModified);
        }
        finally{window.Close();Pump(window);}
    }
    [AvaloniaFact]public async Task ExactSurfaceHitAndCtrlShiftSelectionStayLinkedToTheEditor()
    {
        using var files=new DiskDocumentTests();var s=await files.Open(Fixture());var window=new DiskVisualWindow(files.Shell);window.Show();
        try
        {
            Pump(window);window.GetVisualDescendants().OfType<CheckBox>().Single(c=>Equals(c.Content,"Floating selection details")).IsChecked=false;
            var graph=Named<DskVisualCanvas>(window,"VisualSurfaceCanvas");var band=new DskSurfaceRenderModel(s.Snapshot,0,false).Bands[0];var span=band.Sectors[0];
            double angle=(span.Start+span.End)*Math.PI,r=(band.Inner+band.Outer)/2,radius=Math.Min(graph.Bounds.Width/2,graph.Bounds.Height)/2-20;
            var point=new Point(graph.Bounds.Width/4+Math.Sin(angle)*r*radius,graph.Bounds.Height/2-Math.Cos(angle)*r*radius);
            Assert.Equal(span.Address,graph.Hit(point)!.Value.Sector);graph.ActivateAt(point,false);Assert.Equal(span.Address,s.SelectedSector!.Sector.Address);
            Assert.True(window.RequestGesture(0,new(0,1),false,KeyModifiers.Control));Assert.Equal(2,s.SectorSelection.Count);
            Assert.True(window.RequestGesture(0,new(0,3),false,KeyModifiers.Shift));Assert.Equal(DskSectorSelection.Next([new(0,0),new(0,1)],new(0,1),new(0,3),s.Snapshot.Tracks.Where(t=>t.SlotSide==0).SelectMany(t=>t.Sectors).Select(t=>t.Address).ToArray(),false,true).OrderBy(a=>a.Track).ThenBy(a=>a.Sector),s.SectorSelection.OrderBy(a=>a.Track).ThenBy(a=>a.Sector));
            s.SelectSector(s.Sectors.Single(row=>row.Track==2&&row.Descriptor==1));Assert.Equal(2,window.SelectedTrack);Assert.Equal(s.SelectedSector!.Sector.Address,window.Selection);
            window.RequestGesture(2,null,false,KeyModifiers.None);Assert.Empty(s.SectorSelection);Assert.Null(window.Selection);
        }
        finally{window.Close();Pump(window);}
    }
    [AvaloniaFact]public async Task IssueFiltersNavigateRealFindingsAndExposeAllTrackColumns()
    {
        using var files=new DiskDocumentTests();var disk=Fixture();disk.Image.Tracks[0]!.Sectors[1].FdcStatus1=0x20;disk.MarkModified();var s=await files.Open(disk);var window=new DiskVisualWindow(files.Shell);window.Show();
        try
        {
            Pump(window);window.ShowView("Issues");Pump(window);var table=Named<TableView>(window,"VisualIssues");Assert.Equal(s.Snapshot.Issues.Count,table.ItemCount);
            var severity=Named<ComboBox>(window,"VisualSeverityFilter");severity.SelectedItem="Warning";Assert.All(table.Items.OfType<DskAnalysisIssue>(),i=>Assert.Equal(DskIssueSeverity.Warning,i.Severity));
            severity.SelectedIndex=0;var issue=s.Snapshot.Issues.First(i=>i.Track==0&&i.Sector==1);table.SelectedItem=issue;
            window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Jump to issue")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);
            Assert.Equal(1,Named<TabControl>(window,"DskVisualTabs").SelectedIndex);Assert.Equal(new DskSectorAddress(0,1),s.SelectedSector!.Sector.Address);
            Assert.Equal(new[]{"Descriptor","C","H","R","N","Stored B","Declared B","ST1","ST2","Role","Owner","CP/M user","Issues","Descriptor offset","Data offset"},Named<TableView>(window,"VisualSectors").Columns.Select(c=>c.Header));Capture(window,"avalonia-dsk-track.png");
            var sectors=Named<TableView>(window,"VisualSectors");sectors.SelectedItem=s.Snapshot.Tracks[0].Sectors[1];Assert.Contains("Descriptor\tC\tH\tR",DskTableBehavior.CopyText(sectors));Assert.Contains("1\t0\t0\t2",DskTableBehavior.CopyText(sectors));
            Assert.True(sectors.Columns.Single(c=>Equals(c.Header,"Role")).Width.Value>sectors.Columns.Single(c=>Equals(c.Header,"C")).Width.Value);
            var header=sectors.GetVisualDescendants().OfType<TableViewColumnHeader>().Single(h=>Equals(h.Column?.Header,"R"));var click=header.TranslatePoint(new Point(header.Bounds.Width/2,header.Bounds.Height/2),window)!.Value;
            for(int n=0;n<2;n++){window.MouseDown(click,MouseButton.Left);window.MouseUp(click,MouseButton.Left);Pump(window);}
            Assert.Equal(16,sectors.Items.OfType<DskVisualSector>().First().R);Assert.Equal(new DskSectorAddress(0,1),s.SelectedSector!.Sector.Address);
            window.ShowView("Tracks");Pump(window);Assert.Equal(12,Named<TableView>(window,"VisualTracks").Columns.Count);Capture(window,"avalonia-dsk-tracks.png");
            window.ShowView("Statistics");Pump(window);Assert.Contains(Named<TableView>(window,"VisualStatistics").Items.Cast<object>(),row=>Equals(row.GetType().GetProperty("Category")!.GetValue(row),"Current image SHA-256"));Capture(window,"avalonia-dsk-statistics.png");
            window.ShowView("Issues");Pump(window);Capture(window,"avalonia-dsk-issues.png");
        }
        finally{window.Close();Pump(window);}
    }
    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task FloatingDetailsCanOpenCloseAndReopenAndMfmUsesActualTrack(bool physical)
    {
        using var files=new DiskDocumentTests();var disk=Fixture();if(physical)disk=DiskFormatService.SetNewContainer(disk,HfeVersion.V3);var s=await files.Open(disk,physical?"disk.hfe":"disk.dsk");s.SelectSector(s.Sectors[0]);byte[] before=s.SerializeContainer();
        var owner=new MainWindow(files.Shell);owner.Show();var window=new DiskVisualWindow(files.Shell);Assert.Empty(window.OwnedWindows);window.Show(owner);
        try
        {
            Pump(window);var floating=Assert.Single(window.OwnedWindows);Assert.Equal((physical?s.Snapshot.SourceFormat:"DSK")+" selection details — read-only",floating.Title);Assert.IsType<DskReportView>(floating.GetVisualDescendants().OfType<DskReportView>().Single());floating.Close();
            var check=window.GetVisualDescendants().OfType<CheckBox>().Single(c=>Equals(c.Content,"Floating selection details"));Assert.False(check.IsChecked);check.IsChecked=true;Pump(window);Assert.Single(window.OwnedWindows);
            Named<DskVisualCanvas>(window,"VisualSurfaceCanvas").ZoomBy(8);Assert.True(window.GetVisualDescendants().OfType<CheckBox>().Single(c=>Equals(c.Content,"Unrolled MFM detail")).IsChecked);Named<DskVisualCanvas>(window,"VisualSurfaceCanvas").Fit();
            DskMfmDetailView? detail=null;for(int i=0;i<200;i++){Pump(window);detail=window.GetVisualDescendants().OfType<DskMfmDetailView>().SingleOrDefault();if(detail?.Track is not null)break;await Task.Delay(10,TestContext.Current.CancellationToken);}
            Assert.NotNull(detail?.Track);Assert.Equal(physical,detail.Stored);var track=detail.Track!;Assert.Equal(s.Snapshot.Generate(0,default).PackedBitCells,track.PackedBitCells);detail.FitSector(0);detail.ZoomBy(80);Capture(window,physical?"avalonia-hfe-mfm.png":"avalonia-dsk-mfm.png");Assert.True(detail.RenderedBytes>0);Assert.True(detail.RenderedCells>0);
            Assert.Equal(before,s.SerializeContainer());Assert.False(s.IsModified);
        }
        finally{window.Close();owner.Close();Pump(window);Assert.Empty(window.OwnedWindows);}
    }
    [AvaloniaFact]public async Task DiskMapRestoresWpfSplitIssuesCountsClearSelectionAndStructuredDetails()
    {
        using var files=new DiskDocumentTests();var s=await files.Open(Fixture());var window=new MainWindow(files.Shell){Width=1320,Height=600};window.Show();
        try
        {
            Pump(window);s.ViewIndex=1;Pump(window);var tabs=Named<TabControl>(window,"MapInspectorTabs");Assert.Equal(new[]{"Blocks","Issues"},tabs.Items.OfType<TabItem>().Select(t=>t.Header));
            Assert.Equal(new[]{"Track","R","Role","File / details","Bytes","Offset"},Named<TableView>(window,"DiskSectors").Columns.Select(c=>c.Header));
            s.SelectSector(s.Sectors[0]);Pump(window);Assert.True(Named<Button>(window,"DiskClearSelection").IsEnabled);Assert.Contains("Physical track",Named<DskReportView>(window,"MapSectorDetails").Text);
            var sectors=Named<TableView>(window,"DiskSectors");sectors.SelectedItem=s.Sectors[0];Assert.Contains("0\t1\t",DskTableBehavior.CopyText(sectors));
            Named<Button>(window,"MapWarningsCount").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);Assert.Equal("Warning",s.IssueFilter);Assert.Equal(1,tabs.SelectedIndex);
            Named<Button>(window,"MapAllCount").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);Assert.Equal("All",s.IssueFilter);
            Named<Button>(window,"DiskClearSelection").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);Assert.Empty(s.SectorSelection);
            tabs.SelectedIndex=0;Capture(window,"avalonia-dsk-map.png");
        }
        finally{window.Close();Pump(window);}
    }
    [AvaloniaFact]public async Task InspectorCannotReplaceAPendingHexDraftAndRefreshesAfterCommittedChange()
    {
        using var files=new DiskDocumentTests();var s=await files.Open(Fixture());s.SelectSector(s.Sectors[0]);var window=new DiskVisualWindow(files.Shell);window.Show();
        try
        {
            Pump(window);var original=s.SelectedSector!.Sector.Address;s.HexText="00";Assert.False(window.RequestGesture(0,new(0,1),false,KeyModifiers.None));Assert.Equal(original,s.SelectedSector.Sector.Address);Assert.True(s.HasHexDraft);s.ResetDraft();files.Coordinator.ClearError();
            var replacement=s.Model.Clone();replacement.FileSystem.Insert("SECOND",[4,5],1,0x5000,0x5000);replacement.MarkModified();s.Apply(replacement,s.Revision);Pump(window);Assert.Same(s.Snapshot,window.Snapshot);Assert.Contains(window.Snapshot.Sectors.SelectMany(t=>t.Owners),o=>o.FileName.Contains("SECOND"));
        }
        finally{window.Close();Pump(window);}
    }
}
