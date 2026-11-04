using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;
using MZTools.Platform.Audio;

namespace MZTools.Desktop.Tests;

public sealed class DeepWpfParityTests
{
    [AvaloniaFact]public async Task DskMapUsesOriginalFixedCellsAndLogicalBlockOrdering()
    {
        using var files=new DiskDocumentTests();var session=await files.Open(DskDocumentFactory.CreateFsmz(false));byte[] before=session.SerializeContainer();
        var map=new DiskMap{Session=session,MinHeight=0};var window=new Window{Width=800,Height=400,Content=new ScrollViewer{Content=map}};window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            Assert.True(map.DesiredSize.Height>400,"All track rows retain 25 DIP height; a scrollbar provides access instead of collapsing them.");
            foreach(bool logical in new[]{false,true})
            {
                session.LogicalOrder=logical;
                var track=session.Layout!.Tracks[0];var expected=logical?track.Sectors.OrderBy(s=>s.LogicalBlocks.Count==0?int.MaxValue:s.LogicalBlocks.Min()).ThenBy(s=>s.PhysicalIndex).First():track.Sectors[0];
                Assert.Equal(expected.Address,map.Hit(new Point(125,35))!.Sector.Address);
            }
            Capture(window,"avalonia-dsk-map.png");Assert.Equal(before,session.SerializeContainer());Assert.False(session.IsModified);
        }
        finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    private static string Output => Environment.GetEnvironmentVariable("MZTOOLS_WPF_PARITY_OUTPUT")??Path.Combine(Path.GetTempPath(),"mztools-deep-parity");
    private static byte[] Capture(Control control,string name)
    {
        Directory.CreateDirectory(Output);control.UpdateLayout();
        using var bitmap=new RenderTargetBitmap(new PixelSize((int)control.Bounds.Width,(int)control.Bounds.Height),new Vector(96,96));bitmap.Render(control);
        bitmap.Save(Path.Combine(Output,name),PngBitmapEncoderOptions.Default);
        int size=bitmap.PixelSize.Width*bitmap.PixelSize.Height*4;var address=System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try{bitmap.CopyPixels(new PixelRect(bitmap.PixelSize),address,size,bitmap.PixelSize.Width*4);var bytes=new byte[size];System.Runtime.InteropServices.Marshal.Copy(address,bytes,0,size);return bytes;}
        finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(address);}
    }
    [AvaloniaFact]public void PhysicalQuickDiskMapUsesEightRowsAndExactWpfCoordinateHitTesting()
    {
        using var files=new AudioDocumentTests();
        var tape=new TapeSession(new TapeDocument{Format=TapeDocumentFormat.QdFlashFloppy,QuickDiskProfile=QuickDiskPhysicalProfile.FlashFloppy,IsModified=true});files.Coordinator.Add(tape);
        var window=new MainWindow(files.Shell);window.Show();
        try
        {
            tape.ViewIndex=1;Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var map=window.GetVisualDescendants().OfType<RegionMap>().Single();var layout=tape.Snapshot!.Layout;
            Assert.Equal(8,RegionMap.WrappedRows);Assert.Equal(279,map.Bounds.Height);
            for(int row=0;row<8;row++)
            {
                double x=110+(Math.Max(600,map.Bounds.Width)-122)*.5;var point=new Point(x,(row+1)*31+10);
                double coordinate=(row+.5)*layout.Length/8;
                Assert.Equal(layout.Regions.Single(r=>r.Start<=coordinate&&r.End>coordinate),map.HitWrapped(point));
                Assert.Null(map.HitWrapped(new Point(x,(row+1)*31+28)));
            }
            Assert.Empty(window.GetVisualDescendants().OfType<QuickDiskForensicView>());
            var blocks=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="QuickDiskBlocks");Assert.Equal(layout.Regions.Count,blocks.ItemCount);
            Assert.Equal(new[]{"Role","#","File / details","Start","Length*","Payload B","LOAD","EXEC"},blocks.Columns.Select(c=>c.Header));
            blocks.SelectedItem=layout.Regions[0];Assert.Equal(layout.Regions[0],tape.SelectedRegion);
            tape.SelectRegion(null);Capture(window,"avalonia-quickdisk-map.png");
            Assert.Equal(layout.Summary,tape.WpfMapSummary);Assert.Contains("Gaps are not necessarily free space",tape.WpfMapDetail);
        }
        finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public void SpiralUsesTheOriginal32TurnGeometryAndMfmRetainsLogicalUnits()
    {
        using var files=new AudioDocumentTests();var tape=new TapeSession(new TapeDocument{Format=TapeDocumentFormat.QdFlashFloppy,QuickDiskProfile=QuickDiskPhysicalProfile.FlashFloppy,IsModified=true});files.Coordinator.Add(tape);
        var window=new QuickDiskVisualWindow(files.Shell);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var surface=window.GetVisualDescendants().OfType<QuickDiskSurfaceCanvas>().Single();var mfm=window.GetVisualDescendants().OfType<QuickDiskVisualCanvas>().Single();
            foreach(double fraction in new[]{.01,.2,.5,.8,.99})
            {
                double position=tape.Snapshot!.Layout.Length*fraction;Assert.InRange(Math.Abs(surface.PositionAt(surface.PointAt(position))!.Value-position),0,.00001);
            }
            surface.ZoomBy(2);surface.Fit();Assert.Equal(1,surface.Zoom);
            Assert.Equal(tape.Snapshot!.Layout.Length,mfm.Length);Capture(window,"avalonia-quickdisk-visual.png");
            var tabs=window.GetVisualDescendants().OfType<TabControl>().Single();tabs.SelectedIndex=1;Dispatcher.UIThread.RunJobs();
            var regions=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.IsEffectivelyVisible);
            regions.SelectedItems!.Add(tape.Snapshot.Layout.Regions[0]);regions.SelectedItems.Add(tape.Snapshot.Layout.Regions[1]);
            Assert.Equal(2,surface.Selection.Count);Assert.Equal(surface.Selection,mfm.Selection);
        }
        finally{foreach(var child in window.OwnedWindows.ToArray())child.Close();window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task WavAndFlacRenderIdenticalPcmWithoutOpaqueSelectionAndNeverChangeSources()
    {
        using var files=new AudioDocumentTests();string wav=files.Wave();string flac=files.FilePath("same-pcm.flac");NativeAudioCodecs.Configure();CorePlatformServices.EncodeFlac(wav,flac,TestContext.Current.CancellationToken);
        byte[] originalWav=File.ReadAllBytes(wav),originalFlac=File.ReadAllBytes(flac);
        var wave=await files.Open(wav);var lossless=await files.Open(flac);
        Assert.Equal(2,wave.ChannelMode);Assert.False(wave.AutoScale);Assert.False(wave.HasVisualSelection);
        byte[]? first=null;
        foreach(var session in new[]{wave,lossless})
        {
            session.ShowRange(100,400);await session.RefreshViewportAsync(cancellation:TestContext.Current.CancellationToken);
            var chart=new AudioChart{Kind=AudioPlotKind.Waveform,Session=session};var window=new Window{Width=1200,Height=190,Content=chart};window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();window.UpdateLayout();var pixels=Capture(chart,session==wave?"avalonia-waveform-wav.png":"avalonia-waveform-flac.png");
                int white=0,blue=0,orange=0;for(int i=0;i<pixels.Length;i+=4){if(pixels[i]>245&&pixels[i+1]>245&&pixels[i+2]>245)white++;if(pixels[i]>pixels[i+2]+30)blue++;if(pixels[i+2]>pixels[i]+60&&pixels[i+1]>50)orange++;}
                Assert.True(white>pixels.Length/8,"The PCM graph must retain its white plotting area, without an opaque full-record selection.");Assert.True(blue>100);Assert.True(orange>100);
                if(first is null)first=pixels;else Assert.Equal(first,pixels);
            }
            finally{window.Close();Dispatcher.UIThread.RunJobs();}
        }
        Assert.Equal(originalWav,File.ReadAllBytes(wav));Assert.Equal(originalFlac,File.ReadAllBytes(flac));
    }
    [AvaloniaFact]public async Task AudioAnalysisRestoresWpfNavigationAndEveryScalarBlockColumn()
    {
        using var files=new AudioDocumentTests();var session=await files.Open();var window=new AnalysisWindow();window.Show();
        try
        {
            Assert.True(await window.OpenPathAsync(session.Path!));var audio=window.Model.ActiveAudio!;audio.ShowRange(100,400);await audio.RefreshViewportAsync(cancellation:TestContext.Current.CancellationToken);Dispatcher.UIThread.RunJobs();
            Assert.False(window.Model.InspectorVisible);
            var tabs=window.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="AudioTabs");
            Assert.Equal(new[]{"Records / blocks","Pulse statistics","Channels / levels","Detector agreement","Issues","Regions","Definitions"},tabs.Items.OfType<TabItem>().Take(7).Select(t=>t.Header));
            Assert.Equal(7,tabs.Items.OfType<TabItem>().Count(t=>double.IsNaN(t.Width)||t.Width>0));
            var table=window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="AudioBlocks");
            Assert.Contains(table.Columns,c=>Equals(c.Header,"Reference Name"));Assert.Contains(table.Columns,c=>Equals(c.Header,"Recorded Checksum"));Assert.Contains(table.Columns,c=>Equals(c.Header,"Speed Offset Percent"));
            Capture(window,"avalonia-audio-analysis.png");
            string mzf=files.FilePath("not-audio.mzf");File.WriteAllBytes(mzf,MediaDocumentTests.Mzf("NOT AUDIO"));
            Assert.False(await window.OpenPathAsync(mzf));Assert.Same(audio,window.Model.ActiveAudio);Assert.Contains("WAV, FLAC",window.Model.Documents.Error);
        }
        finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
}
