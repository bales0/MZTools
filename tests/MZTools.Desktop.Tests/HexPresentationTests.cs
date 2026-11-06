using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class HexPresentationTests
{
    private static void Pump(Window window){Dispatcher.UIThread.RunJobs();window.UpdateLayout();}
    private static T Named<T>(Window window,string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(t=>t.Name==name);
    private static string Text(TextBlock text)=>string.Concat(text.Inlines!.OfType<Run>().Select(r=>r.Text));
    private static void Capture(Window window,string name)
    {
        string output=Environment.GetEnvironmentVariable("MZTOOLS_HEX_OUTPUT")??Path.Combine(Path.GetTempPath(),"mztools-hex-presentation");Directory.CreateDirectory(output);Pump(window);
        using var bitmap=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96));bitmap.Render(window);bitmap.Save(Path.Combine(output,name),PngBitmapEncoderOptions.Default);
    }
    [AvaloniaTheory][InlineData(1)][InlineData(15)][InlineData(16)][InlineData(17)][InlineData(33)]
    public void PartialRowsKeepHexAsciiAndSharpAsciiAtFixedWidths(int length)
    {
        byte[] bytes=Enumerable.Range(0,length).Select(i=>(byte)(i%3==0?0x91:i%3==1?0x80:0x41)).ToArray();
        for(int offset=0;offset<bytes.Length;offset+=16)
        {
            var row=new HexViewWindow.Row(bytes,offset,Math.Min(16,length-offset),"Raw");var cells=Enumerable.Range(0,3).Select(i=>HexViewWindow.ByteCell(row,i)).ToArray();
            Assert.Equal(52,Text(cells[0]).Length);Assert.Equal(16,Text(cells[1]).Length);Assert.Equal(16,Text(cells[2]).Length);
            Assert.StartsWith(row.Ascii,Text(cells[1]));Assert.StartsWith(row.Shascii,Text(cells[2]));Assert.All(cells,c=>{Assert.Equal(HexViewWindow.DumpFont,c.FontFamily);Assert.Equal(12,c.FontSize);Assert.Equal(TextWrapping.NoWrap,c.TextWrapping);});
        }
    }
    [AvaloniaFact]public async Task RecordViewerHasOneViewWithActualPerByteWpfColorsAndAllSourceBytes()
    {
        using var files=new MediaDocumentTests();byte[] bytes=MediaDocumentTests.Mzf("COLORED",33).Concat(new byte[]{0x91,0x80,0x0D}).ToArray();File.WriteAllBytes(files.FilePath("colors.mzf"),bytes);var tape=await files.Open("colors.mzf");var window=new HexViewWindow(tape.Rows[0],true);window.Show();
        try
        {
            Pump(window);Assert.Empty(window.GetVisualDescendants().OfType<TabControl>());var table=Named<TableView>(window,"HexDumpTable");var rows=table.ItemsSource!.Cast<HexViewWindow.Row>().ToArray();
            Assert.Equal(bytes,rows.Where(r=>!r.IsQdf).SelectMany(r=>r.Bytes.AsSpan(r.Offset,r.Count).ToArray()));
            var framed=rows.Where(r=>r.IsQdf).ToArray();Assert.Equal(70,framed.Sum(r=>r.Count));Assert.True(SharpQdFrameCodec.HasValidCrc(framed[0].Bytes));
            var firstRuns=HexViewWindow.ByteCell(framed[0],0).Inlines!.OfType<Run>().ToArray();Assert.All(firstRuns.Take(4),r=>Assert.Same(HexViewWindow.PrefixBrush,r.Background));Assert.Same(Brushes.Transparent,firstRuns[5].Background);
            var lastRuns=HexViewWindow.ByteCell(framed[^1],0).Inlines!.OfType<Run>().ToArray();Assert.Same(HexViewWindow.CrcBrush,lastRuns[5].Background);Assert.Same(HexViewWindow.CrcBrush,lastRuns[6].Background);
            Assert.All(HexViewWindow.ByteCell(rows[^1],2).Inlines!.OfType<Run>(),r=>Assert.Same(HexViewWindow.TrailingBrush,r.Foreground));
            Assert.Equal(bytes,File.ReadAllBytes(tape.Path!));Assert.False(tape.IsModified);Capture(window,"hex-viewer-colored.png");
            window.Width=650;Pump(window);Assert.True(table.Columns.Sum(c=>c.Width.Value)>table.Bounds.Width);Capture(window,"hex-viewer-narrow.png");
        }
        finally{window.Close();Pump(window);}
    }
    [AvaloniaTheory][InlineData("Mzf")][InlineData("M12")][InlineData("Mzt")][InlineData("Qdf")]
    public async Task ActualRecordHexUsesTheOpenedContainerAndNeverAddsQdfToMzf(string format)
    {
        using var files=new MediaDocumentTests();var shell=new MZTools.Desktop.ViewModels.ShellViewModel(files.Coordinator,_=>Task.CompletedTask,files.Dialogs);var initial=await files.Open();var target=Enum.Parse<TapeDocumentFormat>(format);string name="context"+TapeSavePolicy.Extension(target);
        byte[] source=TapeDocumentIO.Serialize(initial.Model,target,true);File.WriteAllBytes(files.FilePath(name),source);var tape=await files.Open(name);Assert.Equal(format,tape.Format);Assert.Same(tape,shell.ActiveTape);var owner=new MainWindow(shell);owner.Show();
        try
        {
            Pump(owner);var table=Named<TableView>(owner,"RecordsTable");table.SelectedItem=tape.Rows[0];Pump(owner);table.ContextMenu!.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"Hex view...")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump(owner);
            var hex=Assert.Single(owner.OwnedWindows);var rows=Named<TableView>(hex,"HexDumpTable").ItemsSource!.Cast<HexViewWindow.Row>().ToArray();Assert.Equal(format=="Qdf",rows.Any(r=>r.IsQdf));
            if(format!="Qdf"){Assert.Equal(128,rows.Where(r=>r.Section=="MZF header").Sum(r=>r.Count));Assert.DoesNotContain(hex.GetVisualDescendants().OfType<TextBlock>(),t=>t.Inlines?.OfType<Run>().Any(run=>run.Text?.Contains("QDF")==true)==true);}
            Assert.Equal(source,File.ReadAllBytes(tape.Path!));Assert.False(tape.IsModified);Capture(hex,"hex-context-"+format+".png");hex.Close();
        }
        finally{owner.Close();Pump(owner);}
    }
    [AvaloniaFact]public async Task HexEditorAlignsEveryRowAndSharesScrollingAcrossTheThreeRepresentations()
    {
        using var files=new DiskDocumentTests();var s=await files.Open(DskDocumentFactory.CreateFsmz(false));s.SelectSector(s.Sectors.First(row=>row.Role.Contains("Free")));byte[] before=s.SerializeContainer();var owner=new MainWindow(files.Shell);owner.Show();s.ViewIndex=3;Pump(owner);
        var window=owner.OwnedWindows.Single(w=>w.Title=="Sector Hex Editor");
        try
        {
            var boxes=new[]{Named<TextBox>(window,"DiskHex"),Named<TextBox>(window,"DiskHexAscii"),Named<TextBox>(window,"DiskHexSharpAscii")};
            Assert.All(boxes,box=>{Assert.True(box.IsReadOnly);Assert.Equal(18,box.LineHeight);Assert.Equal(12,box.FontSize);Assert.Equal(boxes[0].FontFamily,box.FontFamily);Assert.Equal(boxes[0].Padding,box.Padding);Assert.Equal(boxes[0].Margin,box.Margin);});
            window.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Unlock HEX editing…")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(window);Assert.True(s.HexEditingUnlocked);
            boxes[0].Focus();boxes[0].CaretIndex=0;window.KeyTextInput("4191");Pump(window);Assert.StartsWith("A.",boxes[1].Text);Assert.StartsWith("A ",boxes[2].Text);
            Assert.Equal(boxes[0].Text!.Split('\n').Length,boxes[1].Text!.Split('\n').Length);Assert.Equal(boxes[1].Text!.Split('\n').Length,boxes[2].Text!.Split('\n').Length);
            void Aligned()
            {
                double[] top=boxes.Select(box=>box.GetVisualDescendants().OfType<TextPresenter>().Single().TranslatePoint(default,window)!.Value.Y).ToArray();Assert.InRange(top.Max()-top.Min(),0,.1);
            }
            Pump(window);Aligned();Capture(window,"hex-editor-aligned.png");
            window.MinHeight=250;window.Height=330;window.Width=700;Pump(window);var scroll=Named<ScrollViewer>(window,"DiskHexScroll");Assert.True(scroll.Extent.Height>scroll.Viewport.Height);scroll.Offset=new Vector(0,72);Pump(window);Assert.True(scroll.Offset.Y>0);Aligned();Capture(window,"hex-editor-scrolled.png");
            Assert.Equal(before,s.SerializeContainer());s.ResetDraft();Pump(window);Assert.Equal(s.HexAscii,boxes[1].Text);Assert.Equal(s.HexSharpAscii,boxes[2].Text);
        }
        finally{s.ResetDraft();window.Close();owner.Close();Pump(owner);}
    }
}
