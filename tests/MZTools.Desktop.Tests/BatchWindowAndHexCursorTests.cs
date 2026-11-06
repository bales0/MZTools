using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class BatchWindowAndHexCursorTests
{
    private static T Find<T>(Window w,string name)where T:Control=>w.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
    private static void Pump(Window w){Dispatcher.UIThread.RunJobs();w.UpdateLayout();}
    private static void Operation(BatchProcessWindow w,BatchOperation operation)
    {var box=Find<ComboBox>(w,"BatchOperation");box.SelectedItem=box.Items.Cast<BatchProcessWindow.OperationChoice>().Single(c=>c.Value==operation);Pump(w);}
    private static void Capture(Window w,string name)
    {
        if(Environment.GetEnvironmentVariable("MZTOOLS_BATCH_HEX_OUTPUT") is not{} output)return;Directory.CreateDirectory(output);Pump(w);using var bitmap=new RenderTargetBitmap(new PixelSize((int)w.Bounds.Width,(int)w.Bounds.Height),new Vector(96,96));bitmap.Render(w);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);
    }
    private static async Task Confirm(BatchProcessWindow w,Task work,string? decline=null)
    {
        for(int i=0;i<3000&&!work.IsCompleted;i++)
        {
            Pump(w);foreach(var dialog in w.OwnedWindows.ToArray())
            {
                string choice=dialog.Title==decline?"Cancel":"Continue";var button=dialog.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>Equals(b.Content,choice));button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            await Task.Delay(10,TestContext.Current.CancellationToken);
        }
        Assert.True(work.IsCompleted);await work;Assert.Null(w.Model.Documents.Error);
    }
    [AvaloniaFact]
    public async Task MenuOpensOnePersistentBatchWithWpfDefaultsAndReachableFooter()
    {
        using var f=new AudioDocumentTests();var owner=new MainWindow(f.Shell);owner.Show();Pump(owner);
        try
        {
            Find<Button>(owner,"ToolsButton").ContextMenu!.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"Batch Process...")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump(owner);var w=Assert.IsType<BatchProcessWindow>(Assert.Single(owner.OwnedWindows));Assert.Empty(w.OwnedWindows);Assert.Equal(BatchOperation.TestIntegrity,w.Operation);
            Assert.Equal(new[]{"Input","DetectedFormat","Filesystem","Operation","Result","Integrity","Reason","Output"},w.InputTable.Columns.Select(c=>c.Header));
            Assert.Contains(Find<ComboBox>(w,"BatchOperation").Items.Cast<object>(),o=>o.ToString()=="Info / catalog");
            foreach(var (width,height) in new[]{(1440,960),(1366,768),(920,640),(640,480)})
            {
                w.MinWidth=0;w.MinHeight=0;w.Width=width;w.Height=height;Pump(w);var button=Find<Button>(w,"StopBatch");var point=button.TranslatePoint(default,w)!.Value;Assert.True(point.Y>=0&&point.Y+button.Bounds.Height<=w.Bounds.Height+.1);Capture(w,$"batch-{width}x{height}.png");
            }
            w.Close();await w.Model.Documents.WaitForIdleAsync();Pump(owner);
        }
        finally{foreach(var child in owner.OwnedWindows.ToArray())child.Close();owner.Close();Pump(owner);}
    }
    [AvaloniaTheory]
    [InlineData("Native")][InlineData("BIN")][InlineData("MZF")][InlineData("M12")][InlineData("MZT")][InlineData("WAV")][InlineData("FLAC")][InlineData("LEP")][InlineData("L16")]
    public async Task LiveWindowPreviewsExecutesAndInvalidatesEveryExtractionChoice(string format)
    {
        using var f=new AudioDocumentTests();string input=f.FilePath("source.mzf");byte[] before=MediaDocumentTests.Mzf("BATCH",32);Array.Clear(before,24,104);File.WriteAllBytes(input,before);var w=new BatchProcessWindow();w.Show();Pump(w);
        try
        {
            w.AddInputs([input,input]);Assert.Single(w.InputTable.Items);Operation(w,BatchOperation.ExtractAll);Find<ComboBox>(w,"ExtractFormat").SelectedItem=format;Find<TextBox>(w,"OutputFolder").Text=f.FilePath("out");Pump(w);
            await w.PreviewAsync();Assert.Null(w.Model.Documents.Error);Assert.Equal(format,w.Session!.Preview.Options.ExtractFormat);Assert.False(Directory.Exists(f.FilePath("out")));Assert.Empty(w.OwnedWindows);
            var snapshot=w.Session;Find<TextBox>(w,"NamingTemplate").Text="{name}_new{ext}";Pump(w);Assert.False(snapshot.CanExecute);Assert.False(w.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Execute preview")).IsEnabled);
            await w.PreviewAsync();Assert.Single(w.Model.Documents.Documents);await Confirm(w,w.ExecuteAsync());Assert.Equal("Completed",w.Session!.Rows.Single().Result);Assert.Equal(before,File.ReadAllBytes(input));Assert.False(w.Session.CanExecute);Capture(w,"batch-executed-"+format+".png");
            if(format is "MZF" or "M12" or "Native")Assert.Equal(before,File.ReadAllBytes(Assert.Single(Directory.GetFiles(w.Session.Rows[0].Output,format=="M12"?"*.m12":"*.mzf"))));
        }
        finally{w.Close();await w.Model.Documents.WaitForIdleAsync();Pump(w);}
    }
    [AvaloniaFact]
    public async Task FolderScanRejectsStaleGenerationAndKeepsUnsupportedInputsVisible()
    {
        using var f=new AudioDocumentTests();foreach(var ext in new[]{"m12","mz0","mz7"})File.WriteAllBytes(f.FilePath("source."+ext),MediaDocumentTests.Mzf("BATCH",32));File.WriteAllText(f.FilePath("bad.txt"),"unsupported");var w=new BatchProcessWindow();w.Show();Pump(w);
        try
        {
            Find<TextBox>(w,"InputFolder").Text=f.Root;Find<TextBox>(w,"ExtensionFilter").Text=".txt";Find<TextBox>(w,"ExtensionFilter").Text=".m12,.mz0,.mz7,.txt";Pump(w);await w.PendingScan;Pump(w);Assert.Equal(4,w.InputTable.Items.Count);
            Assert.Contains(w.InputTable.Items.Cast<BatchRowView>(),r=>r.Result=="Unsupported input");await w.PreviewAsync();Assert.Equal(4,w.Session!.Rows.Count);Assert.Null(w.Model.Documents.Error);
            w.AddInputs([f.FilePath("source.m12")]);await w.PendingScan;Assert.Single(w.InputTable.Items);Assert.False(w.Session.CanExecute);
        }
        finally{w.Close();Pump(w);}
    }
    [AvaloniaTheory][InlineData("None")][InlineData("Auto")][InlineData("Zx0")][InlineData("Zx7")]
    public async Task IntegratedCompressionUsesCoreOptionsAndVerifiedProgramBytes(string algorithm)
    {
        using var f=new AudioDocumentTests();string source=f.FilePath("source.mzf");var before=MediaDocumentTests.Mzf("COMPRESS",512);Array.Clear(before,24,104);File.WriteAllBytes(source,before);var w=new BatchProcessWindow();w.Show();Pump(w);
        try
        {
            w.AddInputs([source]);Operation(w,BatchOperation.CompressPrograms);Find<ComboBox>(w,"CompressionMode").SelectedItem=algorithm;if(algorithm is "Zx0" or "Zx7"){w.GetVisualDescendants().OfType<RadioButton>().Single(r=>Equals(r.Content,"Backward")).IsChecked=true;w.GetVisualDescendants().OfType<CheckBox>().Single(c=>Equals(c.Content,algorithm=="Zx0"?"ZX0 quick":"ZX7 embedded loader")).IsChecked=true;}Find<TextBox>(w,"OutputFolder").Text=f.FilePath("out");Pump(w);Assert.Empty(w.OwnedWindows);await w.PreviewAsync();Assert.Null(w.Model.Documents.Error);Assert.Equal(algorithm,w.Session!.Preview.Options.Compression!.Algorithm.ToString());Assert.Equal(0,w.Session.Preview.Options.Compression.SkipBytes);Assert.Equal(algorithm is "Zx0" or "Zx7"?"Backward":"Forward",w.Session.Preview.Options.Compression.Direction.ToString());await Confirm(w,w.ExecuteAsync());Assert.Equal("Completed",w.Session.Rows.Single().Result);
            string packed=w.Session.Rows[0].Output;var record=new MZTFileReader().ReadStandaloneMzf(packed);bool compressed=MzfLoaderBuilder.TryGetCompressionInfo(record,out _);var restored=compressed?MzfDecompressionService.Decompress(record).Record:record;Assert.Equal(before[128..],restored.Body.MzfBody);Assert.Equal(before,File.ReadAllBytes(source));
            if(compressed)
            {
                w.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Clear file list")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));w.AddInputs([packed]);Operation(w,BatchOperation.DecompressPrograms);await w.PreviewAsync();Assert.Null(w.Model.Documents.Error);await Confirm(w,w.ExecuteAsync());Assert.Equal("Completed",w.Session!.Rows[0].Result);Assert.Equal(before[128..],new MZTFileReader().ReadStandaloneMzf(w.Session.Rows[0].Output).Body.MzfBody);
            }
        }
        finally{w.Close();await w.Model.Documents.WaitForIdleAsync();Pump(w);}
    }
    [AvaloniaFact]
    public void DetailsRetainAllFiveTabsUserTrailingAndUnavailableIntegrity()
    {
        var row=new BatchRow{Input="source.mzf",Result="Will analyze",Integrity="Not verifiable",Contents=[new(1,"PROGRAM",32,"01",0x4000,0x4000,3,"ROM","None",7,"Read-only")],Warnings="First warning\nSecond warning",DetailedReport="Complete technical evidence",SourceHash=new string('A',64)};row.IntegrityChecks.Add(new("Payload","Checksum","Not available","MZF has no payload checksum"));var w=new BatchFileDetailsWindow(row);w.Show();Pump(w);
        try
        {
            var tabs=Find<TabControl>(w,"BatchDetailTabs");Assert.Equal(5,tabs.Items.Count);Assert.Equal(3,tabs.SelectedIndex);tabs.SelectedIndex=0;Pump(w);var contents=Find<TableView>(w,"BatchContents");Assert.Contains(contents.Columns,c=>Equals(c.Header,"User"));Assert.Contains(contents.Columns,c=>Equals(c.Header,"Trailing (B)"));contents.SelectedItem=contents.Items[0];Assert.Contains("0x4000",DskTableBehavior.CopyText(contents));Assert.Contains("7",DskTableBehavior.CopyText(contents));Capture(w,"batch-details-contents.png");tabs.SelectedIndex=3;Capture(w,"batch-details-integrity.png");
        }
        finally{w.Close();Pump(w);}
    }
    [AvaloniaFact]
    public async Task ViewerMapsOnlyPayloadToRamWrapsAndHighlightsAllThreeColumns()
    {
        using var f=new MediaDocumentTests();var bytes=MediaDocumentTests.Mzf("RAM",33);bytes[20]=0xF0;bytes[21]=0xFF;File.WriteAllBytes(f.FilePath("ram.mzf"),bytes.Concat(new byte[]{0x91}).ToArray());var s=await f.Open("ram.mzf");var w=new HexViewWindow(s.Rows[0]);w.Show();Pump(w);
        try
        {
            var table=Find<TableView>(w,"HexDumpTable");var rows=table.ItemsSource!.Cast<HexViewWindow.Row>().ToArray();Assert.All(rows.Where(r=>r.Section!="Data"),r=>Assert.Equal("—",r.RamAddress));var data=rows.Where(r=>r.Section=="Data").ToArray();Assert.Equal("0xFFF0",data[0].RamAddress);Assert.Equal("0x0000",data[1].RamAddress);w.SelectByte(data[0],2);Pump(w);Assert.Contains("RAM 0xFFF2",Find<TextBlock>(w,"HexCursorPosition").Text);
            Assert.True(w.GetVisualDescendants().OfType<TextBlock>().Count(t=>t.Inlines?.OfType<Run>().Any(r=>r.Background==Brushes.DodgerBlue)==true)>=3);Capture(w,"hex-ram-cursor.png");
            var cells=table.GetVisualDescendants().OfType<TextBlock>().Where(t=>ReferenceEquals(t.DataContext,data[0])&&t.Inlines?.OfType<Run>().Count()>=16).OrderBy(t=>t.TranslatePoint(default,w)!.Value.X).ToArray();Assert.Equal(3,cells.Length);
            double glyph=new FormattedText("0",System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(HexViewWindow.DumpFont),12,Brushes.Black).WidthIncludingTrailingWhitespace;
            for(int representation=0;representation<3;representation++){int character=representation==0?16:5;var point=cells[representation].TranslatePoint(new Point(character*glyph+1,5),w)!.Value;w.MouseDown(point,MouseButton.Left);w.MouseUp(point,MouseButton.Left);Pump(w);Assert.Contains("RAM 0xFFF5",Find<TextBlock>(w,"HexCursorPosition").Text);}
        }
        finally{w.Close();Pump(w);}
    }
    [AvaloniaFact]
    public async Task CloseDuringCommitRequestsCancellationAndAwaitsCleanup()
    {
        using var f=new AudioDocumentTests();string source=f.FilePath("source.mzf");File.WriteAllBytes(source,MediaDocumentTests.Mzf("BATCH",32));var before=File.ReadAllBytes(source);var w=new BatchProcessWindow();w.Show();Pump(w);
        w.AddInputs([source]);Operation(w,BatchOperation.ConvertFormat);Find<ComboBox>(w,"ConversionTarget").SelectedItem="M12";Find<TextBox>(w,"OutputFolder").Text=f.FilePath("out");Pump(w);await w.PreviewAsync();Assert.Null(w.Model.Documents.Error);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var row=w.Session!.Preview.Rows[0];var verify=row.VerifyOutput!;
        row.VerifyOutput=(bytes,ct)=>{verify(bytes,ct);entered.TrySetResult();ct.WaitHandle.WaitOne();try{ct.ThrowIfCancellationRequested();}finally{cleanup.TrySetResult();}};
        var execution=w.ExecuteAsync();Pump(w);var confirmation=Assert.Single(w.OwnedWindows);confirmation.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Continue")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await entered.Task.WaitAsync(TimeSpan.FromSeconds(20),TestContext.Current.CancellationToken);
        w.Close();Assert.True(w.IsVisible);await execution;await cleanup.Task;await w.Model.Documents.WaitForIdleAsync();Pump(w);await Task.Delay(50,TestContext.Current.CancellationToken);Pump(w);Assert.False(w.IsVisible);Assert.False(File.Exists(row.Output));Assert.Equal(before,File.ReadAllBytes(source));Assert.Empty(Directory.GetFiles(f.FilePath("out"),"*.tmp"));
    }
    [AvaloniaFact]
    public async Task SectorEditorShowsRowAddressesAndSynchronizesCharacterCursors()
    {
        using var f=new DiskDocumentTests();var s=await f.Open();s.SelectedSector=s.Sectors.First(row=>row.Role.Contains("Free"));var owner=new MainWindow(f.Shell);owner.Show();s.ViewIndex=3;Pump(owner);var w=owner.OwnedWindows.Single(x=>x.Title=="Sector Hex Editor");
        try
        {
            var hex=Find<TextBox>(w,"DiskHex");var ascii=Find<TextBox>(w,"DiskHexAscii");var mz=Find<TextBox>(w,"DiskHexSharpAscii");Assert.StartsWith("00000000 / ",Find<TextBox>(w,"DiskHexOffsets").Text);hex.Focus();hex.CaretIndex=HexPositions.TextIndex(hex.Text!,17,true);Pump(w);Assert.Equal(17,HexPositions.CharacterIndex(ascii.Text!,ascii.SelectionStart,false));Assert.Equal(17,HexPositions.CharacterIndex(mz.Text!,mz.SelectionStart,false));
            mz.Focus();mz.CaretIndex=HexPositions.TextIndex(mz.Text!,5,false);Pump(w);Assert.Equal(5,HexPositions.CharacterIndex(hex.Text!,hex.SelectionStart,true));Assert.Equal(5,HexPositions.CharacterIndex(ascii.Text!,ascii.SelectionStart,false));Assert.Contains("RAM —",Find<TextBlock>(w,"DiskHexPosition").Text);Capture(w,"hex-editor-cursor-addresses.png");
            byte[] before=s.SelectedSector.Sector.Data.ToArray();w.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Unlock HEX editing…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(w);hex.Focus();w.KeyTextInput("41");Pump(w);var changed=DskHexEditService.ParseHex(s.HexText,before.Length);Assert.Equal(0x41,changed[5]);Assert.Equal(before[6],changed[6]);Assert.Equal(before,s.SelectedSector.Sector.Data);s.ResetDraft();
        }
        finally{w.Close();owner.Close();Pump(owner);}
    }
}
