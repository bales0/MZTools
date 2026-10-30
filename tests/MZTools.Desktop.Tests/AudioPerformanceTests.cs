using System.Diagnostics;
using System.Text.Json;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Tests;

public sealed class AudioPerformanceTests
{
    private sealed class CancelProgress(CancellationTokenSource source):IProgress<WavAnalysisProgress>
    {public void Report(WavAnalysisProgress value){if(value.Stage.Contains("FFT",StringComparison.Ordinal))source.Cancel();}}
    [Fact]public async Task FiveMinuteStereoStreamingNavigationAndCancellationRemainBounded()
    {
        using var files=new AudioDocumentTests();const int frames=48000*300;string path=files.FilePath("long-stereo.wav");
        using(var output=File.Create(path))using(var writer=new BinaryWriter(output))
        {
            writer.Write("RIFF"u8);writer.Write(36+frames*4);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)2);writer.Write(48000);writer.Write(192000);writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(frames*4);
            var block=new byte[65536];for(int i=0;i<block.Length/4;i++){short value=(short)Math.Round(12000*Math.Sin(2*Math.PI*(i%32)/32));System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(i*4),value);System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(i*4+2),(short)-value);}
            for(long bytes=0;bytes<frames*4L;bytes+=block.Length)writer.Write(block,0,(int)Math.Min(block.Length,frames*4L-bytes));
        }
        long before=GC.GetTotalMemory(true);var timer=Stopwatch.StartNew();var s=await files.Open(path);long init=timer.ElapsedMilliseconds;var navigation=new List<long>();
        for(int i=0;i<12;i++){timer.Restart();s.ShowRange(i*48000L,i*48000L+48000*20);await s.RefreshViewportAsync(cancellation:TestContext.Current.CancellationToken);Assert.InRange(s.DisplayWaveform.VisibleBuckets(s.ViewStart,s.ViewEnd,1200).Count,1,1201);navigation.Add(timer.ElapsedMilliseconds);}
        s.ShowRange(123456,123488);await s.RefreshViewportAsync(cancellation:TestContext.Current.CancellationToken);Assert.Equal(32,s.DisplayWaveform.VisibleBuckets(123456,123488,1200).Count);
        s.SetSelection([(0,frames)]);using var cancel=CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);timer.Restart();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>s.ComputeSpectralAsync(true,new CancelProgress(cancel),cancel.Token));long cancellation=timer.ElapsedMilliseconds;Assert.Null(s.SpectrogramData);Assert.InRange(s.SpectralCacheBytes,0,24L*1024*1024);
        long managed=GC.GetTotalMemory(true)-before;await s.StopPreviewAsync();s.Dispose();using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Assert.Equal(44+frames*4L,exclusive.Length);
        if(Environment.GetEnvironmentVariable("MZTOOLS_F6_OUTPUT") is { } root){Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"audio-performance.json"),JsonSerializer.Serialize(new{frames,seconds=300,channels=2,fileBytes=44+frames*4L,initialAnalysisMilliseconds=init,panMilliseconds=navigation,spectrogramCancellationIncludingFirstWindowsMilliseconds=cancellation,managedRetainedDeltaBytes=managed,spectralCacheBytes=s.SpectralCacheBytes,workingSetBytes=Process.GetCurrentProcess().WorkingSet64,sourceHandlesReleased=true},new JsonSerializerOptions{WriteIndented=true}));}
    }
}
