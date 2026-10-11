using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using MZTools;

internal static class Program
{
    private static T Field<T>(object target,string name) => (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Call(object target,string name,params object?[] args) => target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i); if(child is T found) yield return found;
            foreach(var nested in Children<T>(child)) yield return nested;
        }
    }
    private static void Readable(DataGrid grid,string context)
    {
        var viewport=Children<ScrollContentPresenter>(grid).First();
        double header=Children<System.Windows.Controls.Primitives.DataGridColumnHeadersPresenter>(grid).First().ActualHeight;
        int visible=Children<DataGridRow>(grid).Count(row=>row.TransformToAncestor(grid).Transform(new Point(0,0)).Y>=header-1 && row.TransformToAncestor(grid).Transform(new Point(0,row.ActualHeight)).Y<=grid.ActualHeight-17);
        Check(viewport.ActualHeight>=120 && visible>=Math.Min(6,grid.Items.Count),$"{context}: readable table viewport={viewport.ActualHeight}, complete rows={visible}, items={grid.Items.Count}");
        Check(grid.ActualWidth>=600,$"{context}: full-width table {grid.ActualWidth}");
        var window=Window.GetWindow(grid)!;
        Check(grid.TransformToAncestor(window).Transform(new Point(0,grid.ActualHeight)).Y<=window.ActualHeight,$"{context}: viewport remains inside window");
    }
    private static (Point Point,DskSectorAddress Address) SurfaceSample(DskVisualCanvas canvas,int side)
    {
        var band=new DskSurfaceRenderModel(canvas.Snapshot!,side,false).Bands.First(b=>b.Sectors.Count>0);
        var span=band.Sectors[0]; double angle=(span.Start+span.End)*Math.PI, r=(band.Inner+band.Outer)/2;
        int count=canvas.SurfaceSides.Count(); double width=canvas.ActualWidth/count, radius=Math.Min(width,canvas.ActualHeight)/2-20;
        double x=canvas.Side<0?(side+.5)*width:width/2;
        return (new Point(x+Math.Sin(angle)*r*radius,canvas.ActualHeight/2-Math.Cos(angle)*r*radius),span.Address);
    }
    [STAThread]
    private static int Main()
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        app.Resources["TopToolbarButton"]=new Style(typeof(Button));
        app.Resources["TopToolbarSeparator"]=new Style(typeof(Border));
        int result=1;
        app.Startup+=async (_,_)=> {
            try {
                var image=DskImage.CreateUniform(40,2,9,512,1,0x4E,0xE5,"UI smoke");
                var document=DskDocument.Open(image.Serialize()); var before=document.Serialize();
                var editor=new DskEditorControl(); var parent=new Window { Content=editor,Width=1100,Height=760 }; parent.Show();
                editor.LoadDocument(document); Call(editor,"OpenVisualAnalysis","Surface");
                var window=Field<DskVisualInspectorWindow>(editor,"visualInspector");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.IsVisible && window.Snapshot.Tracks.Count==80,"Inspector / track count");
                var diskMenu=Field<Button>(editor,"diskMenuButton").ContextMenu;
                Check(!diskMenu.Items.OfType<MenuItem>().Any(i=>i.Header.ToString()!.StartsWith("Verify Image")),"No duplicate Verify Image menu");
                Check(Field<TextBlock>(editor,"infoText").ToolTip.ToString()!.Contains(ImageVerificationService.Hash(document.Serialize())) && Field<DataGrid>(window,"statistics").Items.Cast<object>().Any(row=>row.ToString()!.Contains(ImageVerificationService.Hash(document.Serialize()))),"Disk information includes current hash and canonical creator-padding check");

                var tools=Children<Button>(editor).Single(b=>b.Content is string text && text.StartsWith("Tools"));
                Check(tools.ContextMenu.Items.OfType<MenuItem>().Select(i=>i.Header.ToString()).SequenceEqual(new[] {"Visual analysis…","MZF/M12 → COM..."}),"Tools has no sector transfer or duplicate Hex/physical views");
                Check(!Children<Button>(window).Any(b=>b.Content is string text && text=="Floating details…") && Children<CheckBox>(window).Any(c=>c.Content is string text && text=="Floating selection details"),"Floating details uses checkbox without duplicate toolbar button");

                Check(window.Width<=SystemParameters.WorkArea.Width && window.Height<=SystemParameters.WorkArea.Height,"Initial window fits desktop work area");
                var first=new DskSectorAddress(0,0); var second=new DskSectorAddress(0,1);
                Check(window.Request(0,first,false),"Initial shared selection");
                Check(Field<DskVisualCanvas>(window,"surfaceCanvas").ContextMenu.Items.OfType<MenuItem>().Any(i=>i.Header.ToString()!.StartsWith("Export / Import")),"Surface has sector transfer context action");
                Field<TabControl>(editor,"dskViews").SelectedIndex=1;
                Check(Field<Button>(editor,"dskExportButton").Visibility==Visibility.Collapsed && Field<Button>(editor,"dskDeleteButton").Visibility==Visibility.Collapsed,"Sector map hides file Export/Delete");
                Field<TabControl>(editor,"dskViews").SelectedIndex=0;
                Check(Field<DskSectorLayout>(editor,"selectedMapSector").Address==first,"Editor shared selection");
                Call(editor,"OpenWritableHex",(object?)null);
                var hex=Field<ContentControl>(editor,"editHexHost").Content as DskHexEditorControl;
                Check(hex!=null,"Hex editor available"); hex!.UnlockEditing(); hex.SetEditedHex("00");
                editor.ConfirmDiscardHexChanges=()=>false;
                Check(!window.Request(0,second,false),"Dirty hex navigation must be refused");
                Check(window.Selection==first && hex.HasPendingChanges,"Canceled selection / buffer preserved");
                Check(!window.Request(1,null,false) && window.SelectedTrack==0,"Track navigation honors dirty guard");
                editor.ConfirmDiscardHexChanges=()=>true; hex.Revert();
                Check(window.Request(0,second,false),"Navigation after revert");
                var canvas=Field<DskVisualCanvas>(window,"surfaceCanvas"); canvas.ZoomBy(3); Check(canvas.Zoom==3,"Zoom");
                canvas.PanBy(new Vector(35,-20)); Check(Field<Vector>(canvas,"pan")==new Vector(35,-20),"Pan");
                canvas.Fit(); Check(canvas.Zoom==1 && Field<Vector>(canvas,"pan")==default,"Fit");
                Field<ComboBox>(window,"sideChoice").SelectedIndex=2;
                Check(canvas.Side==1,"Surface side switch");
                Check(canvas.SurfaceSides.SequenceEqual(new[] {1}) && canvas.Hit(SurfaceSample(canvas,1).Point)?.Sector==SurfaceSample(canvas,1).Address,"Single-side layout and hit identity");
                Field<ComboBox>(window,"role").SelectedItem="Unknown";
                window.Request(1,new(1,0),false);
                Check(Field<DataGrid>(window,"sectors").Items.Count==9,"Filters retained after track change");
                Field<ComboBox>(window,"role").SelectedIndex=0;
                Check(Field<TabControl>(window,"tabs").Items.Cast<TabItem>().Select(t=>t.Header.ToString()).SequenceEqual(new[] {"Disk Surface","Track / Sector","Issues","Statistics","Tracks"}),"Only five tabs with Disk Surface first");
                Check(Field<TabControl>(window,"tabs").SelectedIndex==0 && Field<WrapPanel>(window,"cards").Children.Count==4,"Surface initial view includes summary cards");
                foreach(var grid in new[] {"tracks","sectors","issues","statistics"}) {
                    var table=Field<DataGrid>(window,grid); Check(table.IsReadOnly && table.CanUserResizeColumns,"Read-only resizable grid "+grid);
                }
                window.ShowView("Surface"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Field<ComboBox>(window,"sideChoice").SelectedIndex=0;
                Check(canvas.SurfaceSides.SequenceEqual(new[] {0,1}),"All sides renders both surfaces");
                foreach(int side in canvas.SurfaceSides) {
                    var sample=SurfaceSample(canvas,side); Check(canvas.Hit(sample.Point)?.Sector==sample.Address,"Both-side hit identity");
                    Check(window.RequestGesture(sample.Address.Track,sample.Address,false,side==0?ModifierKeys.None:ModifierKeys.Control),"Both-side selection");
                }
                Check(window.Selections.Count==2 && window.Selections.Select(s=>s.Track%2).Distinct().Count()==2,"Cross-side Ctrl selection retained");
                Call(editor,"OpenWritableHex",(object?)null);
                var sideHex=(DskHexEditorControl)Field<ContentControl>(editor,"editHexHost").Content;
                sideHex.UnlockEditing(); sideHex.SetEditedHex("00"); editor.ConfirmDiscardHexChanges=()=>false;
                var opposite=SurfaceSample(canvas,0).Address;
                Check(!window.RequestGesture(opposite.Track,opposite,false,ModifierKeys.None) && window.Selections.Count==2 && sideHex.HasPendingChanges,"Cross-side dirty switch preserves both selections");
                canvas.ActivateAt(new Point(1,1),false);Check(window.Selections.Count==2 && sideHex.HasPendingChanges,"Outside click respects dirty hex guard");
                sideHex.Revert(); editor.ConfirmDiscardHexChanges=()=>true;
                var zoomSample=SurfaceSample(canvas,1); canvas.ZoomBy(2); canvas.PanBy(new Vector(7,-9));
                var transformed=new Point((zoomSample.Point.X-canvas.ActualWidth/2)*2+canvas.ActualWidth/2+7,(zoomSample.Point.Y-canvas.ActualHeight/2)*2+canvas.ActualHeight/2-9);
                Check(canvas.Hit(transformed)?.Sector==zoomSample.Address,"Both-side hit after zoom and pan"); canvas.Fit();
                Check(canvas.Hit(new Point(canvas.ActualWidth/4,canvas.ActualHeight/2))==null,"Spindle does not select a sector");
                var oneDisk=DskDocument.Open(DskImage.CreateUniform(2,1,3,512,1,0x4E,0xE5,"single").Serialize());
                var oneCanvas=new DskVisualCanvas {Kind=DskVisualCanvas.ViewKind.Surface,Side=-1,Snapshot=new DskVisualSnapshot(oneDisk.Image,DskAnalyzer.Analyze(oneDisk))};
                oneCanvas.Measure(new Size(800,400)); oneCanvas.Arrange(new Rect(0,0,800,400));
                Check(oneCanvas.SurfaceSides.SequenceEqual(new[] {0}) && oneCanvas.Hit(SurfaceSample(oneCanvas,0).Point)?.Sector==SurfaceSample(oneCanvas,0).Address,"Single-sided All sides remains one surface");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var artifact=Path.Combine(Path.GetTempPath(),"MZTools-DskVisualUiSmoke"); Directory.CreateDirectory(artifact);
                var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
                var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using(var output=File.Create(Path.Combine(artifact,"surface.png"))) png.Save(output);
                canvas.ActivateAt(new Point(1,1),false);Check(window.Selections.Count==0,"Empty surface click clears selection");
                var physicalSource=HfeImage.Parse(HfeImage.FromDsk(DskDocumentFactory.CreateRaw(4,2,9,512,1,0x4E,0xE5,"raw")).Serialize(HfeVersion.V3));
                string physicalOriginal=Path.Combine(artifact,"save-as-original.hfe"),physicalCopy=Path.Combine(artifact,"save-as-copy.hfe");
                File.WriteAllBytes(physicalOriginal,physicalSource.Serialize(physicalSource.Version));
                var physicalWindow=new PhysicalTrackViewerWindow(parent,physicalSource,physicalOriginal,_=>{});
                var physicalView=Field<DskVisualInspectorWindow>(physicalWindow,"inspector");
                physicalView.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var physicalMenu=Children<Button>(physicalView).Single(b=>b.Content is string text && text=="Disk / Tools ▾").ContextMenu;
                Check(physicalMenu.Items.OfType<MenuItem>().Any(item=>Equals(item.Header,"Save As…")) && !physicalMenu.Items.OfType<MenuItem>().Any(item=>item.Header.ToString()!.Contains("Convert / Save As")),"Physical inspector separates Save As from conversion exports");
                physicalWindow.SaveAs(physicalCopy);
                Check(physicalView.Title.Contains(physicalCopy) && Field<HfeDocumentSession>(physicalWindow,"session").FilePath==physicalCopy,"Physical Save As switches visible and active path");
                Check(File.ReadAllBytes(physicalOriginal).SequenceEqual(File.ReadAllBytes(physicalCopy)),"Physical Save As preserves original");physicalWindow.Close();
                foreach(var version in new[] {HfeVersion.V1,HfeVersion.V2,HfeVersion.V3}) {
                    var hfeDocument=DskDocumentFactory.CreateFsmz(tracks:4);
                    hfeDocument.FileSystem.Insert("TEST",new byte[] {1,2,3});hfeDocument.MarkModified();
                    var hfe=HfeImage.Parse(HfeImage.FromDsk(hfeDocument).Serialize(version));var originalHfe=hfe.Serialize(version);
                    var hwindow=new DskVisualInspectorWindow(parent,new DskVisualSnapshot(hfe),(_,_)=>true,_=>{},_=>{});
                    hwindow.AddFileList();hwindow.Show();Field<CheckBox>(hwindow,"autoFloating").IsChecked=false;
                    hwindow.Request(0,new DskSectorAddress(0,0),false);Field<CheckBox>(hwindow,"showMfm").IsChecked=true;
                    await Task.Delay(160);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var stored=Field<DskMfmDetailControl>(hwindow,"mfmDetail");
                    Check(stored.Stored && ReferenceEquals(stored.Track,hfe.Tracks[0]),"HFE lower view uses actual stored cells "+version);
                    stored.FitSector(0);stored.ZoomBy(1024);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(stored.RenderedCells>0,"HFE stored cell signal renders at deep zoom");
                    stored.FitSector(0);stored.ZoomBy(16);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(stored.RenderedBytes>0,"HFE decoded byte phase renders inside actual fields");
                    var hbitmap=new RenderTargetBitmap((int)hwindow.ActualWidth,(int)hwindow.ActualHeight,96,96,PixelFormats.Pbgra32);hbitmap.Render(hwindow);
                    var hpng=new PngBitmapEncoder();hpng.Frames.Add(BitmapFrame.Create(hbitmap));using(var output=File.Create(Path.Combine(artifact,"hfe-"+version+".png"))) hpng.Save(output);
                    Check(originalHfe.SequenceEqual(hfe.Serialize(version)),"HFE inspection preserves source bytes");hwindow.Close();
                    var hfePath=Path.Combine(artifact,"editable-"+version+".hfe");File.WriteAllBytes(hfePath,originalHfe);
                    var fileMain=new MainWindow();fileMain.Show();
                    Call(fileMain,"AddFile",hfePath,true,false,null,null,null,false,0,0);
                    var fileEditor=Field<DskEditorControl>(fileMain,"dskEditorControl");var edited=Field<DskDocument>(fileEditor,"document");
                    Check(fileEditor.IsVisible && edited.SourceHfe?.Version==version,"HFE opens in main filebrowser "+version);
                    Check(Field<DataGrid>(fileEditor,"directoryGrid").Items.Count==1 && Field<Button>(fileEditor,"dskAddButton").IsEnabled,"HFE filebrowser exposes editable files");
                    Check(Field<MenuItem>(fileEditor,"formatDiskMenu").IsEnabled,"HFE format action available");
                    Check(!Field<Button>(fileEditor,"dskSaveButton").IsEnabled && Field<Button>(fileEditor,"dskSaveAsButton").IsEnabled,"Unmodified HFE disables Save, keeps Save As");
                    edited.FileSystem.Insert("ADDED",new byte[] {7,8,9});edited.MarkModified();Call(fileEditor,"RefreshView");
                    Check(Field<Button>(fileEditor,"dskSaveButton").IsEnabled,"Modified HFE enables Save");
                    Call(fileEditor,"Save_Click",fileEditor,new RoutedEventArgs());
                    Check(!edited.IsModified && HfeImage.Parse(File.ReadAllBytes(hfePath)).Version==version,"HFE Save keeps original container variant");
                    Check(!Field<Button>(fileEditor,"dskSaveButton").IsEnabled,"Successful HFE Save disables button");
                    Call(fileEditor,"OpenVisualAnalysis","Surface");
                    var physicalInspector=Field<DskVisualInspectorWindow>(fileEditor,"visualInspector");
                    Check(physicalInspector.Snapshot.IsPhysical && physicalInspector.Snapshot.Sectors.Any(s=>s.Owners.Any(o=>o.FileName.Contains("ADDED"))),"Editable HFE uses physical Visual analysis after save");
                    physicalInspector.Close();fileMain.Close();

                }
                _ = app.Dispatcher.BeginInvoke(new Action(()=> {
                    var formatDialog=Application.Current.Windows.OfType<DskNewDialog>().Single();
                    Check(formatDialog.Title.Contains("Format disk"),"Existing disk uses formatting dialog");
                    Check(!Field<ComboBox>(formatDialog,"formatBox").Items.Cast<object>().Any(choice=>choice.ToString()!.Contains("single program")),"Formatting lists filesystem/raw presets");
                    Call(formatDialog,"Create_Click",formatDialog,new RoutedEventArgs());
                }),DispatcherPriority.ApplicationIdle);
                var formatOptions=DskNewDialog.ShowFormatting(parent,4,2);
                Check(formatOptions is {Tracks:4,Sides:2},"Formatting preserves initial geometry for selectable filesystem preset");
                foreach(int variant in new[] {0,1,2,3}) {
                    var newMain=new MainWindow();newMain.Show();
                    _=app.Dispatcher.BeginInvoke(new Action(()=> {
                        var dialog=Application.Current.Windows.OfType<DskNewDialog>().Single();
                        var choices=Field<ComboBox>(dialog,"containerBox");
                        Check(choices.Items.Count==4 && Field<ComboBox>(dialog,"formatBox").Items.Count==11,"New disk offers all DSK/HFE containers and presets");
                        choices.SelectedIndex=variant;Call(dialog,"Create_Click",dialog,new RoutedEventArgs());
                    }),DispatcherPriority.ApplicationIdle);
                    Call(newMain,"button_Click_NewDsk",newMain,new RoutedEventArgs());
                    var newEditor=Field<DskEditorControl>(newMain,"dskEditorControl");var newDoc=Field<DskDocument>(newEditor,"document");
                    Check(newDoc.SourceHfe?.Version==(variant==0?null:(HfeVersion?)variant),"New disk respects chosen container "+variant);
                    Check(newDoc.FilePath==null && newDoc.FileSystem is FsmzFileSystem,"New disk starts unbound with selected filesystem");
                    newDoc.Save(Path.Combine(artifact,"new-image-"+variant+(variant==0?".dsk":".hfe")));Call(newEditor,"RefreshView");newMain.Close();
                }
                foreach(var version in new[] {HfeVersion.V1,HfeVersion.V2,HfeVersion.V3}) foreach(bool multi in new[] {false,true}) {
                    var iplMain=new MainWindow();iplMain.Show();
                    _=app.Dispatcher.BeginInvoke(new Action(()=> {
                        var dialog=Application.Current.Windows.OfType<DskNewDialog>().Single();
                        Field<ComboBox>(dialog,"containerBox").SelectedIndex=(int)version;
                        Field<ComboBox>(dialog,"formatBox").SelectedIndex=multi?1:0;Call(dialog,"Create_Click",dialog,new RoutedEventArgs());
                    }),DispatcherPriority.ApplicationIdle);
                    Call(iplMain,"button_Click_NewDsk",iplMain,new RoutedEventArgs());
                    var iplEditor=Field<DskEditorControl>(iplMain,"dskEditorControl");
                    Check(iplEditor.DocumentTitle.Contains(version==HfeVersion.V1?"HFE":version==HfeVersion.V2?"HFEv2":"HFEv3"),"Empty IPL draft displays selected HFE container");
                    var rows=Field<System.Collections.ObjectModel.ObservableCollection<MultiGameIplRow>>(iplEditor,"multiIplRows");
                    var record=TapeRecord.FromLegacy(new MZQFileHeader {
                        StartSign=[0,0x16,0x16,0xA5],DataSize=64,MzfFtype=1,MzfFname=SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes("IPL TEST".PadRight(16)),
                        MzfFnameEnd=0x0D,Unused1=new byte[2],MzfSize=3,MzfStart=0x1200,MzfExec=0x1200,MzfHeaderDescription=new byte[104],Crc=[67,82,67]
                    },new MZQFileBody {StartSign=[0,0x16,0x16,0xA5],MzfBodySign=5,DataSize=3,MzfBody=[1,2,3],Crc=[67,82,67],TrailingData=[]});
                    rows.Add(new MultiGameIplRow(1,record,"FIRST"));if(multi) rows.Add(new MultiGameIplRow(2,record,"SECOND"));
                    await (Task)Call(iplEditor,"RebuildIplAsync")!;
                    var created=Field<DskDocument>(iplEditor,"document");
                    Check(created.SourceHfe?.Version==version && Field<bool>(iplEditor,"multiIplLayoutValid"),"First IPL build retains HFE container");
                    string path=Path.Combine(artifact,$"new-ipl-{version}-{multi}.hfe");created.Save(path);Call(iplEditor,"RefreshView");
                    rows[0].MenuName="RENAMED";await (Task)Call(iplEditor,"RebuildIplAsync")!;
                    Check(created.SourceHfe?.Version==version && created.FilePath==path && Field<Button>(iplEditor,"dskSaveButton").IsEnabled,"IPL rebuild keeps HFE and destination");
                    Call(iplEditor,"Save_Click",iplEditor,new RoutedEventArgs());
                    var opened=DskDocument.Open(path);
                    Check(opened.SourceHfe?.Version==version && opened.FileSystem.Type==(multi?DskFileSystemType.MultiIpl:DskFileSystemType.SingleIpl) && opened.FileSystem.ReadDirectory()[0].Name=="RENAMED","HFE IPL saves and reopens after rebuild");
                    Check(!Field<Button>(iplEditor,"dskSaveButton").IsEnabled,"IPL Save disables button after success");iplMain.Close();
                }
                var formatSource=DskDocumentFactory.CreateRaw(3,1,3,128,1,0x4E,0xE5,"format source");
                var formatOriginal=formatSource.Serialize();var formattedPreview=DiskFormatting.Create(formatOptions!);
                Check(formattedPreview.FileSystem is FsmzFileSystem && formatOriginal.SequenceEqual(formatSource.Serialize()),"Format preview does not change source");
                editor.LoadDocument(document);
                Check(Field<DskVisualCanvas>(window,"surfaceCanvas").Generated==null,"Reanalysis invalidates generated cache");
                foreach(string format in new[] {"json","csv","txt"}) Check(DskVisualReport.Export(window.Snapshot,format).Length>100,"Report "+format);
                Check(before.SequenceEqual(document.Serialize()) && !document.IsModified,"Inspection remains read-only");
                var owned=DskDocumentFactory.CreateCpm(false); owned.FileSystem.Insert("A.BIN",new byte[4096]); owned.FileSystem.Insert("B.BIN",new byte[4096],user:15); owned.MarkModified();
                var ownedBefore=owned.Serialize(); editor.LoadDocument(owned);
                Field<TabControl>(editor,"dskViews").SelectedIndex=0;
                Check(Field<Button>(editor,"dskExportButton").Visibility==Visibility.Visible && Field<Button>(editor,"dskDeleteButton").Visibility==Visibility.Visible,"File commands return in Files on a filesystem disk");
                var layout=Field<DskLayoutModel>(editor,"diskLayout");
                var a=layout.Sectors.Where(s=>s.Owners.Any(o=>o.FileName=="A.BIN")).ToArray();
                var rawDialog=new DskRawRangeDialog(parent,owned,a.Take(2).Select(s=>s.Address).ToArray()); rawDialog.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var rawTable=Field<DataGrid>(rawDialog,"list");var rawMap=Field<DskDiskMapControl>(rawDialog,"map");
                Check(rawTable.SelectedItems.Count==2,"Sector transfer receives current multiselection");
                Check(Field<TextBlock>(rawDialog,"totals").Text.Contains("1 024") || Field<TextBlock>(rawDialog,"totals").Text.Contains("1,024") || Field<TextBlock>(rawDialog,"totals").Text.Contains("1\u00a0024"),"Sector transfer shows exact selected byte count");
                var mapGesture=Field<Action<DskSectorLayout?,ModifierKeys>>(rawMap,"SelectionRequested");
                mapGesture(a[0],ModifierKeys.None);mapGesture(a[2],ModifierKeys.Control);Check(rawTable.SelectedItems.Count==2,"Transfer map supports Ctrl multi-selection");
                mapGesture(a[0],ModifierKeys.Shift);Check(rawTable.SelectedItems.Count>=3,"Transfer map supports Shift ranges");
                rawTable.UnselectAll();Check(!Field<Button>(rawDialog,"export").IsEnabled && !Field<Button>(rawDialog,"import").IsEnabled,"Empty transfer selection disables file actions");
                rawTable.SelectedItems.Add(rawTable.Items[0]);Check(Field<Button>(rawDialog,"export").IsEnabled && Field<Button>(rawDialog,"import").IsEnabled,"Transfer actions follow table selection");
                var rawOrder=Children<ComboBox>(rawDialog).Single();rawOrder.SelectedIndex=1;
                var orderedRows=rawTable.Items.Cast<DskSectorLayout>().Where(s=>s.Track==0).ToArray();
                Check(orderedRows.Select(s=>s.R).SequenceEqual(orderedRows.Select(s=>s.R).Order()),"Sector ID transfer order is reflected in the table");
                foreach(double scale in new[] {1.0,1.5,2.0}) {
                    rawDialog.Width=850*scale;rawDialog.Height=650*scale;((FrameworkElement)rawDialog.Content).LayoutTransform=new ScaleTransform(scale,scale);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Readable(rawTable,$"Graphical sector transfer at {scale}");Check(rawMap.ActualHeight>100,"Transfer map remains available alongside table");
                }
                ((FrameworkElement)rawDialog.Content).LayoutTransform=Transform.Identity;rawDialog.Width=1050;rawDialog.Height=760;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var rawImage=new RenderTargetBitmap((int)rawDialog.ActualWidth,(int)rawDialog.ActualHeight,96,96,PixelFormats.Pbgra32);rawImage.Render(rawDialog);
                var rawPng=new PngBitmapEncoder();rawPng.Frames.Add(BitmapFrame.Create(rawImage));using(var output=File.Create(Path.Combine(artifact,"sector-transfer.png"))) rawPng.Save(output);
                Check(ownedBefore.SequenceEqual(owned.Serialize()),"Graphical sector transfer selection leaves disk bytes unchanged");rawDialog.Close();
                var unsafeDisk=DskDocumentFactory.CreateFsmz();unsafeDisk.FileSystem.Insert("TEST",new byte[] {1});
                unsafeDisk.Image.Tracks[1]!.Sectors.Single(s=>s.SectorId==16).Data[6]=0xFF;unsafeDisk.MarkModified();
                var readOnlyDisk=DskDocument.Open(unsafeDisk.Serialize());Check(readOnlyDisk.IsReadOnly,"Read-only transfer fixture");
                var readOnlyBefore=readOnlyDisk.Serialize();var readOnlyTransfer=new DskRawRangeDialog(parent,readOnlyDisk,new[] {new DskSectorAddress(0,0)});readOnlyTransfer.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(Field<Button>(readOnlyTransfer,"export").IsEnabled && !Field<Button>(readOnlyTransfer,"import").IsEnabled,"Graphical transfer permits export and blocks import on read-only image");
                Check(readOnlyBefore.SequenceEqual(readOnlyDisk.Serialize()),"Read-only transfer leaves original bytes unchanged");readOnlyTransfer.Close();
                foreach(string choice in new[] {"Overwrite","Rename","Cancel"}) {
                    _=Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=> {
                        var dialog=Application.Current.Windows.Cast<Window>().Single(w=>w.Title=="COM filename already exists");
                        Check(Children<TextBox>(dialog).Single().Text=="GAME~1.COM","Collision dialog suggests available name");
                        if(choice=="Cancel") dialog.Close();
                        else Children<Button>(dialog).Single(b=>Equals(b.Content,choice)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }));
                    var collision=typeof(NativeComDialog).GetMethod("ChooseCollision",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[] {parent,"GAME.COM","GAME~1.COM"});
                    if(choice=="Cancel") Check(collision==null,"Collision cancel performs no import");
                    else {
                        var decision=((DiskCollisionPolicy Policy,string Name))collision!;
                        Check(decision.Policy==(choice=="Overwrite"?DiskCollisionPolicy.Replace:DiskCollisionPolicy.Cancel) && decision.Name==(choice=="Overwrite"?"GAME.COM":"GAME~1.COM"),"Explicit collision action: "+choice);
                    }
                }
                var b=layout.Sectors.Where(s=>s.Owners.Any(o=>o.FileName=="B.BIN")).ToArray();
                Check(editor.ApplySectorSelection(new[] {a[0].Address,a[1].Address,b[0].Address},a[1].Address),"Multi selection");
                Check(editor.SectorSelection.Count==3 && window.Selections.Count==3,"Shared three-sector selection");
                Check(Field<DataGrid>(editor,"mapBlocksGrid").SelectedItems.Count==3,"Blocks multiselect");
                var files=Field<DataGrid>(editor,"directoryGrid"); Field<TabControl>(editor,"dskViews").SelectedIndex=0;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                foreach(var entry in files.Items.OfType<DskFileEntry>()) {
                    var row=(DataGridRow)files.ItemContainerGenerator.ContainerFromItem(entry)!;
                    Check(MapInteraction.IsRelatedProperty.GetMetadata(typeof(DataGridRow))!=null && (bool)row.GetValue(MapInteraction.IsRelatedProperty),"Owning file highlight");
                    Check(((string)row.GetValue(MapInteraction.LinkLabelProperty)).Contains("Partial"),"Partial owner label");
                }
                window.ShowView("Track");
                Check(window.RequestGesture(a[0].Track,a[0].Address,false,ModifierKeys.Control),"Ctrl toggle");
                Check(editor.SectorSelection.Count==2 && !editor.SectorSelection.Contains(a[0].Address),"Ctrl removes one descriptor");
                var range=a.GroupBy(s=>s.Track).First(g=>g.Count()>1).Take(2).ToArray();
                Check(window.RequestGesture(range[0].Track,range[0].Address,false,ModifierKeys.None),"Range anchor");
                Check(window.RequestGesture(range[1].Track,range[1].Address,false,ModifierKeys.Shift),"Shift range");
                Check(editor.SectorSelection.Count>=2,"Shift descriptor range");
                files.UnselectAll(); files.SelectedItem=files.Items.OfType<DskFileEntry>().First(e=>e.Name=="A");
                Check(editor.SectorSelection.ToHashSet().SetEquals(a.Select(s=>s.Address)),"File selection highlights every owned sector");
                Check(editor.ApplySectorSelection(new[] {a[0].Address,a[1].Address},a[0].Address),"Dirty multi baseline");
                Call(editor,"OpenWritableHex",(object?)null); var multiHex=(DskHexEditorControl)Field<ContentControl>(editor,"editHexHost").Content;
                multiHex.UnlockEditing(); multiHex.SetEditedHex("00"); editor.ConfirmDiscardHexChanges=()=>false;
                Check(!window.RequestMany(new[] {b[0].Address},b[0].Address),"Dirty multi switch refused");
                Check(editor.SectorSelection.Count==2 && window.Selections.Count==2 && multiHex.HasPendingChanges,"Whole multi selection and buffer retained");
                Call(editor,"MapBlocks_KeyDown",Field<DataGrid>(editor,"mapBlocksGrid"),new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(parent),0,Key.Escape) { RoutedEvent=Keyboard.PreviewKeyDownEvent });
                Check(editor.SectorSelection.Count==2 && multiHex.HasPendingChanges,"Escape retains selection when dirty discard is refused");
                multiHex.Revert(); editor.ConfirmDiscardHexChanges=()=>true;
                Check(ownedBefore.SequenceEqual(owned.Serialize()),"Multi navigation does not change bytes");
                parent.Height=600; Field<TabControl>(editor,"dskViews").SelectedItem=Field<TabItem>(editor,"mapTab");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var blocks=Field<DataGrid>(editor,"mapBlocksGrid");
                Check(blocks.ActualHeight>=80 && blocks.TransformToAncestor(editor).Transform(new Point(0,blocks.ActualHeight)).Y<=editor.ActualHeight,$"Blocks fits smaller window: table={blocks.ActualHeight}, bottom={blocks.TransformToAncestor(editor).Transform(new Point(0,blocks.ActualHeight)).Y}, editor={editor.ActualHeight}");
                var detail=Field<ReportView>(editor,"sectorDetailText");
                var expander=detail.Parent as Expander; Check(expander!=null,"Collapsible detail");
                double expandedBlocks=blocks.ActualHeight; expander!.IsExpanded=false; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(blocks.ActualHeight>expandedBlocks,"Collapsing detail frees list space");
                var editorBitmap=new RenderTargetBitmap((int)parent.ActualWidth,(int)parent.ActualHeight,96,96,PixelFormats.Pbgra32); editorBitmap.Render(parent);
                var editorPng=new PngBitmapEncoder(); editorPng.Frames.Add(BitmapFrame.Create(editorBitmap));
                using(var output=File.Create(Path.Combine(artifact,"disk-map-selection.png"))) editorPng.Save(output);
                window.ShowView("Surface"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.ShowView("Issues"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var overviewIssues=Field<DataGrid>(window,"issues");
                Check(overviewIssues.Items.Count==window.Snapshot.Issues.Count && overviewIssues.Items.Count>0,"Overview lists every counted finding");
                overviewIssues.SelectedIndex=0;
                Call(window,"ShowSelectionDetails");
                Check(Field<TabControl>(window,"tabs").SelectedIndex==2 && Field<Window>(window,"floatingDetails").IsVisible,"Finding explanation opens floating details without leaving Issues");
                if(overviewIssues.SelectedItem is DskAnalysisIssue global && global.Track==null && global.FileKey==null) {
                    int previousTrack=window.SelectedTrack; Call(window,"NavigateIssue",global);
                    Check(window.SelectedTrack==previousTrack && Field<ReportView>(window,"details").Text.Contains(global.Description),"Global finding shows explanation without inventing track");
                }
                Call(window,"ShowDetails");
                foreach(double scale in new[] {1.0,1.5,2.0}) {
                    window.Width=850*scale; window.Height=600*scale;
                    ((FrameworkElement)window.Content).LayoutTransform=new ScaleTransform(scale,scale);
                    foreach(var page in new[] {("Issues","issues"),("Statistics","statistics"),("Tracks","tracks")}) {
                        window.ShowView(page.Item1); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Readable(Field<DataGrid>(window,page.Item2),$"{page.Item1}, scale {scale}");
                    }
                    window.ShowView("Details"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Field<Window>(window,"floatingDetails").Width=660;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var report=Field<ReportView>(window,"floatingReport");
                    Readable(Children<DataGrid>(report).First(),$"Details, scale {scale}");
                    Check(report.CompactHeader && Children<DataGrid>(report).First().Columns[0].ActualWidth>=200,"Compact report retains readable property names");
                    window.ShowView("Track"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Readable(Field<DataGrid>(window,"sectors"),$"Track table, scale {scale}");
                    var split=(Grid)Field<DataGrid>(window,"sectors").Parent;
                    Check(Children<GridSplitter>(split).Single().ResizeBehavior==GridResizeBehavior.PreviousAndNext,"Explicit splitter neighbors");
                    double beforeTable=Field<DataGrid>(window,"sectors").ActualHeight;
                    var divider=Children<GridSplitter>(split).Single();
                    divider.RaiseEvent(new DragStartedEventArgs(0,0) {RoutedEvent=Thumb.DragStartedEvent});
                    divider.RaiseEvent(new DragDeltaEventArgs(0,-20) {RoutedEvent=Thumb.DragDeltaEvent});
                    divider.RaiseEvent(new DragCompletedEventArgs(0,-20,false) {RoutedEvent=Thumb.DragCompletedEvent});
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(Field<DataGrid>(window,"sectors").ActualHeight>beforeTable,"Divider rows give space to table");
                    split.RowDefinitions[0].Height=new GridLength(1,GridUnitType.Star); split.RowDefinitions[2].Height=new GridLength(2,GridUnitType.Star);
                }
                ((FrameworkElement)window.Content).LayoutTransform=Transform.Identity; window.Width=1250; window.Height=850;
                window.ShowView("Statistics"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var statistics=Field<DataGrid>(window,"statistics");
                Check(statistics.Columns[1].ActualWidth>250 && statistics.Columns[2].ActualWidth>350,"Statistics uses available width for values and explanations");
                window.ShowView("Surface"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var overviewBitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); overviewBitmap.Render(window);
                var overviewPng=new PngBitmapEncoder(); overviewPng.Frames.Add(BitmapFrame.Create(overviewBitmap));
                using(var output=File.Create(Path.Combine(artifact,"graphical-overview.png"))) overviewPng.Save(output);
                Call(window,"ShowDetails");
                foreach(var page in new[] {"Details","Statistics","Track"}) {
                    window.ShowView(page); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var capture=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); capture.Render(window);
                    var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
                    using(var output=File.Create(Path.Combine(artifact,page.ToLowerInvariant()+"-full-page.png"))) encoder.Save(output);
                }
                window.ShowView("Surface"); Check(window.RequestGesture(b[0].Track,b[0].Address,false,ModifierKeys.None),"Select user 15");
                var floating=Field<Window>(window,"floatingDetails");
                Check(floating.IsVisible && floating.Owner==window && !floating.ShowActivated && window.IsEnabled,"Floating detail is modeless and leaves map enabled");
                Check(Field<ReportView>(window,"floatingReport").Text.Contains("user 15"),"User 15 appears in floating detail");
                double left=floating.Left+20,top=floating.Top+20; floating.Left=left; floating.Top=top; floating.Width=640;
                Check(window.RequestGesture(a[0].Track,a[0].Address,false,ModifierKeys.None),"Select user 0");
                Check(floating.Left==left && floating.Top==top && floating.Width==640 && Field<ReportView>(window,"floatingReport").Text.Contains("user 0"),"Floating detail follows user 0 without moving or resizing");
                var overviewSurface=Field<DskVisualCanvas>(window,"surfaceCanvas"); window.ShowView("Surface"); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(overviewSurface.ActualHeight>=40 && overviewSurface.Hit(SurfaceSample(overviewSurface,1).Point)?.Sector==SurfaceSample(overviewSurface,1).Address,"Surface functional both sides");
                window.ShowView("Surface"); Field<CheckBox>(window,"showMfm").IsChecked=true;
                window.RequestGesture(b[0].Track,b[0].Address,false,ModifierKeys.None);
                window.RequestGesture(a[0].Track,a[0].Address,false,ModifierKeys.None);
                await (Task)Call(window,"LoadMfmDetail")!; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var mfm=Field<DskMfmDetailControl>(window,"mfmDetail"); Check(mfm.Track!=null,"Requested MFM track generated");
                Check(mfm.Track!.Cylinder==a[0].Track/window.Snapshot.Sides && mfm.Track.Side==a[0].Track%window.Snapshot.Sides,"Rapid track changes discard stale generation");
                var generated=mfm.Track!; var generatedSector=generated.Sectors[a[0].PhysicalIndex];
                Check(DskMfmDetailControl.ReadByte(generated,generatedSector.DataCell)==a[0].Data[0],"MFM bytes come from selected payload");
                mfm.ZoomBy(16); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(mfm.RenderedBytes>0 && mfm.RenderedBytes<=Math.Ceiling(mfm.ActualWidth/28)+1,"Byte detail rendering bounded by viewport");
                mfm.ZoomBy(8); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(mfm.RenderedCells>0 && mfm.RenderedCells<=Math.Ceiling(mfm.ActualWidth/5)+1,"Bit detail rendering bounded by viewport");
                Check(mfm.Hit(new Point(mfm.ActualWidth/2,60))==a[0].Address,"Unrolled MFM hit preserves physical descriptor identity");
                var band=new DskSurfaceRenderModel(window.Snapshot,0,false).Bands.Single(t=>t.Track==a[0].Track);
                var descriptorSpan=band.Sectors.Single(s=>s.Address==a[0].Address);
                double angle=(descriptorSpan.Start+descriptorSpan.End)*Math.PI;
                double radius=Math.Min(canvas.ActualWidth/2,canvas.ActualHeight)/2-20, radial=(band.Inner+band.Outer)/2;
                var radialPoint=new Point(canvas.ActualWidth/4+Math.Sin(angle)*radial*radius,canvas.ActualHeight/2-Math.Cos(angle)*radial*radius);
                canvas.Fit(); canvas.ZoomBy(8,radialPoint); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(canvas.Hit(radialPoint)?.Sector==a[0].Address,"DSK surface keeps descriptor positions at large zoom despite generated MFM");
                var withMfm=new RenderTargetBitmap((int)canvas.ActualWidth,(int)canvas.ActualHeight,96,96,PixelFormats.Pbgra32);withMfm.Render(canvas);
                canvas.Generated=null;canvas.InvalidateVisual();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var withoutMfm=new RenderTargetBitmap((int)canvas.ActualWidth,(int)canvas.ActualHeight,96,96,PixelFormats.Pbgra32);withoutMfm.Render(canvas);
                byte[] withPixels=new byte[withMfm.PixelWidth*withMfm.PixelHeight*4],withoutPixels=new byte[withPixels.Length];
                withMfm.CopyPixels(withPixels,withMfm.PixelWidth*4,0);withoutMfm.CopyPixels(withoutPixels,withoutMfm.PixelWidth*4,0);
                Check(withPixels.SequenceEqual(withoutPixels) && mfm.Track!=null,"Generated MFM does not change circular rendering; lower detail remains available");canvas.Generated=generated;
                var renderTimer=System.Diagnostics.Stopwatch.StartNew();
                for(int frame=0;frame<20;frame++) { mfm.Pan(1); mfm.UpdateLayout(); var rendered=new RenderTargetBitmap((int)mfm.ActualWidth,(int)mfm.ActualHeight,96,96,PixelFormats.Pbgra32); rendered.Render(mfm); }
                Check(renderTimer.Elapsed<TimeSpan.FromSeconds(5),"Deep MFM viewport rendering stays responsive");
                var mfmImage=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); mfmImage.Render(window);
                var mfmEncoder=new PngBitmapEncoder(); mfmEncoder.Frames.Add(BitmapFrame.Create(mfmImage)); using(var output=File.Create(Path.Combine(artifact,"mfm-deep-detail.png"))) mfmEncoder.Save(output);
                floating.Close(); Check(Field<CheckBox>(window,"autoFloating").IsChecked==false,"Closing floating detail disables automatic reopening");
                window.RequestGesture(b[0].Track,b[0].Address,false,ModifierKeys.None);
                Check(Field<Window?>(window,"floatingDetails")==null,"Closed floating detail stays closed during navigation");
                Check(ownedBefore.SequenceEqual(owned.Serialize()),"Generated MFM, zoom and floating information do not change bytes");
                var oldCache=Field<DskGeneratedTrackCache>(window,"generatedCache");
                editor.LoadDocument(document);
                Check(!ReferenceEquals(oldCache,Field<DskGeneratedTrackCache>(window,"generatedCache")) && mfm.Track==null,"Snapshot replacement invalidates MFM cache and viewport");
                var missingBytes=(byte[])before.Clone(); missingBytes[0x35]=0;
                var missing=DskDocument.Open(missingBytes); editor.LoadDocument(missing);
                var missingIssue=window.Snapshot.Issues.First(i=>i.Track==1 && i.Sector==null);
                window.Request(0,first,false); Call(editor,"OpenWritableHex",(object?)null);
                hex=(DskHexEditorControl)Field<ContentControl>(editor,"editHexHost").Content;
                hex.UnlockEditing(); hex.SetEditedHex("00"); editor.ConfirmDiscardHexChanges=()=>false;
                Call(window,"NavigateIssue",missingIssue);
                Check(window.Selection==first && hex.HasPendingChanges,"Overview issue navigation protects dirty hex");
                Field<DataGrid>(editor,"issuesGrid").SelectedItem=missingIssue;
                Check(window.Selection==first && hex.HasPendingChanges,"Existing Issues also protects dirty hex");
                hex.Revert(); editor.ConfirmDiscardHexChanges=()=>true;
                Field<DataGrid>(editor,"issuesGrid").SelectedItem=missingIssue;
                Check(window.SelectedTrack==1 && window.Selection==null,"Missing-track issue navigates without arbitrary sector");
                Field<DataGrid>(window,"issues").SelectedItem=missingIssue; Call(window,"JumpIssue");
                Check(window.SelectedTrack==1 && window.Selection==null,"Inspector track-level issue navigation");
                Call(window,"NavigateIssue",missingIssue);
                Check(window.SelectedTrack==1 && Field<TabControl>(window,"tabs").SelectedIndex==1,"Finding navigation keeps track visible");
                window.Close(); parent.Close();
                var main=new MainWindow(); main.Show();
                var qd=Field<TapeDocument>(main,"document"); qd.Format=TapeDocumentFormat.QdSharpLegacy;
                qd.QuickDiskSourceImage=QuickDiskLayoutBuilder.BuildPreviewImage(qd); qd.IsModified=false;
                var qdBefore=(byte[])qd.QuickDiskSourceImage.Clone(); Call(main,"RefreshGrid");
                Field<TabItem>(main,"quickDiskMapTab").IsSelected=true; Call(main,"RefreshQuickDiskMap");
                var qdBlocks=Field<DataGrid>(main,"quickDiskBlocks"); var qdReport=Field<ReportView>(main,"quickDiskMapDetail");
                qdBlocks.SelectedItem=Field<QuickDiskLayout>(main,"quickDiskLayout").Regions.First(r=>r.Kind==QuickDiskRegionKind.Gap);
                foreach(double scale in new[] {1.0,1.5,2.0}) {
                    main.Width=1100*scale; main.Height=600*scale; ((FrameworkElement)main.Content).LayoutTransform=new ScaleTransform(scale,scale);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var reportGrid=Children<DataGrid>(qdReport).First(); var viewport=Children<ScrollContentPresenter>(reportGrid).First();
                    Check(qdReport.CompactHeader && viewport.ActualHeight>=50,$"QD detail readable at scale {scale}: {viewport.ActualHeight}");
                    Check(qdReport.TransformToAncestor(main).Transform(new Point(0,qdReport.ActualHeight)).Y<=main.ActualHeight,"QD detail remains inside window");
                    var split=(Grid)qdReport.Parent; var divider=Children<GridSplitter>(split).Single(); double beforeDetail=qdReport.ActualHeight;
                    divider.RaiseEvent(new DragStartedEventArgs(0,0) {RoutedEvent=Thumb.DragStartedEvent});
                    divider.RaiseEvent(new DragDeltaEventArgs(0,-20) {RoutedEvent=Thumb.DragDeltaEvent});
                    divider.RaiseEvent(new DragCompletedEventArgs(0,-20,false) {RoutedEvent=Thumb.DragCompletedEvent});
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Check(qdReport.ActualHeight>beforeDetail,"QD splitter increases detail space");
                    split.RowDefinitions[0].Height=new GridLength(2,GridUnitType.Star); split.RowDefinitions[2].Height=new GridLength(3,GridUnitType.Star);
                }
                Check(qdBefore.SequenceEqual(qd.QuickDiskSourceImage!) && !qd.IsModified,"QD layout does not modify source image");
                ((FrameworkElement)main.Content).LayoutTransform=Transform.Identity;main.Width=1100;main.Height=700;
                TapeRecord Record(string name,int size)=>TapeRecord.FromLegacy(new MZQFileHeader {
                    StartSign=[0,0x16,0x16,0xA5],DataSize=64,MzfFtype=1,MzfFname=SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(name.PadRight(16)),
                    MzfFnameEnd=0x0D,Unused1=new byte[2],MzfSize=(ushort)size,MzfStart=0x1200,MzfExec=0x1200,MzfHeaderDescription=new byte[104],Crc=[67,82,67]
                },new MZQFileBody {StartSign=[0,0x16,0x16,0xA5],MzfBodySign=5,DataSize=(ushort)size,MzfBody=Enumerable.Range(0,size).Select(i=>(byte)i).ToArray(),Crc=[67,82,67],TrailingData=[]});
                qd.Records.Add(Record("FIRST",64));qd.Records.Add(Record("SECOND",96));qd.QuickDiskSourceImage=QuickDiskLayoutBuilder.BuildPreviewImage(qd);Call(main,"RefreshGrid");
                Call(main,"OpenQuickDiskVisual");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var qwindow=Field<QuickDiskVisualInspectorWindow>(main,"quickDiskVisual");
                Check(Field<MenuItem>(main,"qdVisualAnalysisMenu").Visibility==Visibility.Visible && Field<Button>(main,"toolsButton").Visibility==Visibility.Visible,"QD Tools visual analysis entry");
                Field<DataGrid>(main,"MzfDataGrid").SelectedIndex=0;
                Check(qwindow.Selection.Count>1 && qwindow.Selection.All(r=>r.FileIndex==0),"Main file selection highlights every owned region");
                var secondPayload=qwindow.Snapshot.Layout.Regions.Single(r=>r.FileIndex==1 && r.Kind==QuickDiskRegionKind.Payload);
                qwindow.SelectRegions(new[] {secondPayload},secondPayload,true);
                Check(Field<DataGrid>(main,"MzfDataGrid").SelectedIndex==1 && qwindow.Selection.Single()==secondPayload,"Region selection selects owner file without replacing region focus");
                qwindow.SelectFiles(new[] {0,1},true);Check(Field<DataGrid>(main,"MzfDataGrid").SelectedItems.Count==2,"Multiple related files synchronized");
                var logicalCanvas=Field<QuickDiskVisualCanvas>(qwindow,"canvas");var regionGesture=Field<Action<QuickDiskRegion?,ModifierKeys>>(logicalCanvas,"Selected");
                Check(!Children<Button>(qwindow).Any(button=>button.Content?.ToString()?.StartsWith("Export selected")==true),"QD selected-region export is context-only");
                foreach(var target in new FrameworkElement[] {logicalCanvas,Field<QuickDiskSurfaceCanvas>(qwindow,"surfaceCanvas"),Field<DataGrid>(qwindow,"regions")})
                    Check(target.ContextMenu!.Items.OfType<MenuItem>().Any(item=>item.Header.ToString()!.StartsWith("Export selected region")),"QD region export available in surface, MFM and regions menus");
                var qsurface=Field<QuickDiskSurfaceCanvas>(qwindow,"surfaceCanvas");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(qsurface.ActualHeight>100 && logicalCanvas.ActualHeight>=140 && logicalCanvas.TranslatePoint(new Point(),qsurface).Y>qsurface.ActualHeight,"QD circular surface above resizable unrolled detail");
                var logicalPayload=qwindow.Snapshot.Layout.Regions.First(r=>r.Kind==QuickDiskRegionKind.Payload);
                Check(qsurface.Hit(qsurface.PointAt(logicalPayload.Start+logicalPayload.Length*.5))==logicalPayload,"QD circular projection maps logical bytes back to exact region");
                void CheckSpiral()
                {
                    double total=qsurface.Snapshot!.Layout.Length;
                    for(int step=1;step<1024;step++) {
                        double position=total*step/1024;
                        var point=qsurface.PointAt(position);
                        Check(qsurface.PositionAt(point) is double actual && Math.Abs(actual-position)<1e-6,"Spiral position round-trips across all turns");
                        var expected=qsurface.Snapshot.Layout.Regions.FirstOrDefault(r=>r.Start<=position && position<r.End);
                        if(expected!=null && Math.Abs(expected.Start-position)>1e-6 && Math.Abs(expected.End-position)>1e-6)
                            Check(qsurface.Hit(point)==expected,"Spiral hit retains exact region identity");
                    }
                    for(int turn=1;turn<32;turn++) {
                        double seam=total*turn/32,epsilon=total*1e-9;
                        Check((qsurface.PointAt(seam-epsilon)-qsurface.PointAt(seam+epsilon)).Length<.01,"Spiral has no radial jump between turns");
                        Check(Math.Abs(qsurface.PositionAt(qsurface.PointAt(seam-epsilon))!.Value-(seam-epsilon))<1e-6 && Math.Abs(qsurface.PositionAt(qsurface.PointAt(seam+epsilon))!.Value-(seam+epsilon))<1e-6,"Spiral inverse selects correct turn at angular seam");
                    }
                    var a=qsurface.PointAt(total*.25/32);var b=qsurface.PointAt(total*1.25/32);
                    Check(qsurface.Hit(new Point((a.X+b.X)/2,(a.Y+b.Y)/2))==null,"Space between spiral turns selects no data");
                    var gap=new Point((a.X+b.X)/2,(a.Y+b.Y)/2);
                    var gapBitmap=new RenderTargetBitmap((int)qsurface.ActualWidth,(int)qsurface.ActualHeight,96,96,PixelFormats.Pbgra32);gapBitmap.Render(qsurface);
                    if(gap.X>=0 && gap.Y>=0 && gap.X<gapBitmap.PixelWidth && gap.Y<gapBitmap.PixelHeight) {
                        var pixel=new byte[4];gapBitmap.CopyPixels(new Int32Rect((int)gap.X,(int)gap.Y,1,1),pixel,4,0);
                        Check(pixel[0]<110 && pixel[1]<110 && pixel[2]<110,$"QD spiral gap is black: {pixel[0]}/{pixel[1]}/{pixel[2]}");
                    }
                }
                CheckSpiral();
                qwindow.SelectRegions(new[] {logicalPayload},logicalPayload,false);logicalCanvas.FitSelection();logicalCanvas.Zoom(128);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(logicalCanvas.RenderedCells>0 && logicalCanvas.RenderedTransitions>0,"Logical QD generates a digital MFM signal at cell zoom");
                Field<CheckBox>(qwindow,"showMfm").IsChecked=false;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!logicalCanvas.IsVisible && qsurface.ActualHeight>100,"QD unrolled detail collapses to release surface area");
                Field<CheckBox>(qwindow,"showMfm").IsChecked=true;
                var firstPayload=qwindow.Snapshot.Layout.Regions.First(r=>r.Kind==QuickDiskRegionKind.Payload);
                regionGesture(firstPayload,ModifierKeys.None);regionGesture(secondPayload,ModifierKeys.Control);
                Check(qwindow.Selection.Count==2,"QD Ctrl toggles independent regions");
                regionGesture(firstPayload,ModifierKeys.Shift);Check(qwindow.Selection.Count>2,"QD Shift selects contiguous region range");
                var qfloat=Field<Window>(qwindow,"detailWindow");double position=qfloat.Left+25;qfloat.Left=position;qfloat.Width=650;
                qwindow.SelectRegions(new[] {secondPayload},secondPayload,true);Check(Math.Abs(qfloat.Left-position)<1 && qfloat.Width==650 && !qfloat.ShowActivated,$"QD floating detail retains position and size: left={qfloat.Left}, wanted={position}, width={qfloat.Width}, activate={qfloat.ShowActivated}");
                foreach(double scale in new[] {1.0,1.5,2.0}) {
                    qwindow.Width=850*scale;qwindow.Height=600*scale;((FrameworkElement)qwindow.Content).LayoutTransform=new ScaleTransform(scale,scale);
                    Field<TabControl>(qwindow,"tabs").SelectedIndex=0;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);CheckSpiral();
                    foreach(var page in new[] {(1,"regions"),(2,"files"),(3,"findings")}) {
                        Field<TabControl>(qwindow,"tabs").SelectedIndex=page.Item1;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Readable(Field<DataGrid>(qwindow,page.Item2),$"QD inspector {page.Item2} at {scale}");
                    }
                    Field<TabControl>(qwindow,"tabs").SelectedIndex=4;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Readable(Children<DataGrid>(Field<ReportView>(qwindow,"statistics")).First(),"QD statistics");
                }
                ((FrameworkElement)qwindow.Content).LayoutTransform=Transform.Identity;qwindow.Width=1100;qwindow.Height=700;
                qd.Format=TapeDocumentFormat.QdHxc;qd.QuickDiskSourceImage=QuickDiskLayoutBuilder.BuildPreviewImage(qd);var physicalBefore=(byte[])qd.QuickDiskSourceImage.Clone();Call(main,"RefreshGrid");
                Check(qwindow.Snapshot.Layout.IsPhysical,"Existing QD inspector refreshes for physical format");
                var payload=qwindow.Snapshot.Layout.Regions.First(r=>r.Kind==QuickDiskRegionKind.Payload);
                qwindow.SelectRegions(new[] {payload},payload,false);var qcanvas=Field<QuickDiskVisualCanvas>(qwindow,"canvas");Field<TabControl>(qwindow,"tabs").SelectedIndex=0;
                qcanvas.FitSelection();qcanvas.Zoom(8);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(qcanvas.RenderedBytes>0 && qcanvas.RenderedBytes<=Math.Ceiling(qcanvas.ActualWidth/32)+2,"QD HEX visits only viewport bytes");
                qcanvas.Zoom(8);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(qcanvas.RenderedCells>0 && qcanvas.RenderedCells<=Math.Ceiling(qcanvas.ActualWidth/6)+2,"QD physical cells visit only viewport range");
                Check(qcanvas.RenderedTransitions>0,"QD stored cell signal includes real transitions");
                Check(qsurface.Hit(qsurface.PointAt(payload.Start+payload.Length*.5))==payload,"QD circular physical-cell hit matches table region");
                Field<Action<QuickDiskRegion?,ModifierKeys>>(qsurface,"Selected")(payload,ModifierKeys.None);
                Check(qwindow.Selection.Single()==payload && qcanvas.Selection.Single()==payload,"QD surface and bottom MFM share selection");
                double at=payload.Start+payload.Length*.5;var pointer=qsurface.PointAt(at);qsurface.ZoomBy(2,pointer);
                Check((qsurface.PointAt(at)-pointer).Length<.01 && qsurface.Hit(pointer)==payload,"QD circular zoom preserves pointer and region");qsurface.Fit();
                CheckSpiral();qsurface.ZoomBy(60,qsurface.PointAt(at));CheckSpiral();qsurface.Fit();
                qcanvas.Zoom(64);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var renderWatch=System.Diagnostics.Stopwatch.StartNew();
                for(int pass=0;pass<20;pass++) {var render=new RenderTargetBitmap((int)qcanvas.ActualWidth,(int)qcanvas.ActualHeight,96,96,PixelFormats.Pbgra32);render.Render(qcanvas);}
                Check(renderWatch.Elapsed.TotalSeconds<5,"QD deep render stays bounded");
                var center=new Point(qcanvas.ActualWidth/2,60);Check(qcanvas.Hit(center)?.FileIndex==payload.FileIndex,"Deep QD hit retains original owner identity");
                double cursor=(.25),location=qcanvas.Start+qcanvas.Length*cursor;qcanvas.Zoom(.5,cursor);Check(Math.Abs(qcanvas.Start+qcanvas.Length*cursor-location)<1,"QD cursor zoom anchors position");
                var qbitmap=new RenderTargetBitmap((int)qwindow.ActualWidth,(int)qwindow.ActualHeight,96,96,PixelFormats.Pbgra32);qbitmap.Render(qwindow);
                var qpng=new PngBitmapEncoder();qpng.Frames.Add(BitmapFrame.Create(qbitmap));using(var output=File.Create(Path.Combine(artifact,"qd-visual-detail.png"))) qpng.Save(output);
                qfloat.Close();qwindow.SelectRegions(new[] {payload},payload,false);Check(Field<Window?>(qwindow,"detailWindow")==null,"QD closed floating detail does not reopen");
                Check(physicalBefore.SequenceEqual(qd.QuickDiskSourceImage!) && !qd.IsModified,"QD visual inspection preserves stored image and modified state");
                qwindow.ShowUnavailable("Test unavailable preview");Check(!Field<TabControl>(qwindow,"tabs").IsEnabled,"Stale QD preview cannot navigate");Call(main,"RefreshGrid");
                Check(Field<TabControl>(qwindow,"tabs").IsEnabled,"Valid QD refresh restores navigation");
                qd.Clear();Call(main,"RefreshGrid");Check(Field<QuickDiskVisualInspectorWindow?>(main,"quickDiskVisual")==null,"QD inspector closes when document changes type");main.Close();
                foreach(var format in new[] {TapeDocumentFormat.Qdf,TapeDocumentFormat.Mzq,TapeDocumentFormat.QdHxc,TapeDocumentFormat.QdFlashFloppy,TapeDocumentFormat.QdSharpLegacy,TapeDocumentFormat.Mzf,TapeDocumentFormat.M12,TapeDocumentFormat.Mzt}) {
                    var saveMain=new MainWindow();saveMain.Show();var tape=Field<TapeDocument>(saveMain,"document");
                    tape.Records.Add(Record("SAVE TEST",64));tape.Format=format;tape.IsModified=true;
                    void AnswerTapeOptions(bool accept=true) {
                        if(format is not (TapeDocumentFormat.Mzf or TapeDocumentFormat.M12 or TapeDocumentFormat.Mzt)) return;
                        _=app.Dispatcher.BeginInvoke(new Action(()=> {
                            var options=Application.Current.Windows.OfType<SaveOptionsDialog>().Single();
                            if(accept) Call(options,"SaveButton_Click",options,new RoutedEventArgs());else options.DialogResult=false;
                        }),DispatcherPriority.ApplicationIdle);
                    }
                    string extension=format switch {TapeDocumentFormat.Qdf=>".qdf",TapeDocumentFormat.Mzq=>".mzq",TapeDocumentFormat.Mzf=>".mzf",TapeDocumentFormat.M12=>".m12",TapeDocumentFormat.Mzt=>".mzt",_=>".qd"};
                    string original=Path.Combine(artifact,"native-original-"+format+extension),copy=Path.Combine(artifact,"native-copy-"+format+extension);
                    AnswerTapeOptions();Call(saveMain,"SaveNativeTapeDocument",original,format);byte[] sourceBytes=File.ReadAllBytes(original);
                    AnswerTapeOptions();Call(saveMain,"SaveNativeTapeDocument",copy,format);
                    Check(tape.FilePath==copy && !Field<Button>(saveMain,"saveButton").IsEnabled && Field<Button>(saveMain,"saveAsButton").IsEnabled,"Native Save As switches path and disables Save "+format);
                    if(format is TapeDocumentFormat.Mzf or TapeDocumentFormat.M12) tape.Records[0]=Record("CHANGED",32);
                    else tape.Records.Add(Record("SECOND",32));
                    tape.IsModified=true;Call(saveMain,"RefreshGrid");
                    Check(Field<Button>(saveMain,"saveButton").IsEnabled,"Modified native document enables Save "+format);
                    if(format is TapeDocumentFormat.Mzf or TapeDocumentFormat.M12 or TapeDocumentFormat.Mzt) {
                        string cancelled=Path.Combine(artifact,"cancelled-"+format+extension);if(File.Exists(cancelled)) File.Delete(cancelled);
                        AnswerTapeOptions(false);Call(saveMain,"SaveNativeTapeDocument",cancelled,format);
                        Check(tape.IsModified && tape.FilePath==copy && !File.Exists(cancelled) && Field<Button>(saveMain,"saveButton").IsEnabled,"Cancelled tape save retains active destination and dirty flag "+format);
                    }
                    string blockedDirectory=Path.Combine(artifact,"blocked-save-directory");File.WriteAllText(blockedDirectory,"This is a file, not an output directory.");
                    try {AnswerTapeOptions();Call(saveMain,"SaveNativeTapeDocument",Path.Combine(blockedDirectory,format+extension),format);throw new InvalidOperationException("Blocked directory should reject save "+format);}
                    catch(TargetInvocationException exception) when(exception.InnerException is IOException) {}
                    Check(tape.IsModified && tape.FilePath==copy && Field<Button>(saveMain,"saveButton").IsEnabled,"Failed native save keeps dirty flag, active path and button "+format);
                    AnswerTapeOptions();Call(saveMain,"button_Click_Save",saveMain,new RoutedEventArgs());
                    Check(!tape.IsModified && !Field<Button>(saveMain,"saveButton").IsEnabled && sourceBytes.SequenceEqual(File.ReadAllBytes(original)) && !sourceBytes.SequenceEqual(File.ReadAllBytes(copy)),"Native Save after Save As updates copy only and disables button "+format);saveMain.Close();
                }
                Console.WriteLine("PASS: Save As destination, physical export separation and modified Save buttons; QD visual analysis entries, bidirectional multi-selection, Ctrl/Shift, floating details, physical HEX/cells and cursor zoom, viewport render bounds, layout scaling and snapshot lifecycle; existing DSK verification, QD map, dirty-hex and read-only checks.");
                Console.WriteLine("Screenshot: "+Path.Combine(artifact,"surface.png")); result=0;
            } catch(Exception exception) { Console.Error.WriteLine(exception); }
            finally { app.Shutdown(); }
        };
        app.Run(); return result;
    }
}
