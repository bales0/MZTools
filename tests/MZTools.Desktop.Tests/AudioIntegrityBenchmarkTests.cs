using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Tests;

// Opt-in integration measurement: each fixture is thirty minutes, never an original recording.
public sealed class AudioIntegrityBenchmarkTests
{
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes; }
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessIoCounters(IntPtr process,out IoCounters counters);
    private static ulong Reads() => OperatingSystem.IsWindows() && GetProcessIoCounters(Process.GetCurrentProcess().Handle,out var io)?io.ReadBytes:0;
    [Fact]
    public async Task ThirtyMinuteMediaOperations()
    {
        string? label=Environment.GetEnvironmentVariable("MZTOOLS_SHA256_BENCH");
        Assert.SkipUnless(label is not null,"Set MZTOOLS_SHA256_BENCH=baseline or after for the thirty-minute benchmark.");
        string output=System.IO.Path.GetFullPath("TestResults/Sha256Audit-2026-11-05/results");Directory.CreateDirectory(output);
        using var files=new AudioDocumentTests();var results=new List<object>();
        async Task<object> Measure(string operation,Func<Task> action)
        {
            using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;ulong reads=Reads();var watch=Stopwatch.StartNew();
            await action();watch.Stop();process.Refresh();
            return new {operation,milliseconds=watch.Elapsed.TotalMilliseconds,cpuMilliseconds=(process.TotalProcessorTime-cpu).TotalMilliseconds,logicalReadBytes=Reads()-reads,processLifetimePeakWorkingSetBytes=process.PeakWorkingSet64};
        }
        foreach(var (bits,channels) in new[]{(16,1),(16,2),(24,1),(24,2)})
        {
            string wav=files.FilePath($"thirty-{bits}-{channels}.wav");WriteWave(wav,bits,channels);
            var paths=new List<string>{wav};
            if(channels==2){MZTools.Platform.Audio.NativeAudioCodecs.Configure();string flac=System.IO.Path.ChangeExtension(wav,"flac");CorePlatformServices.EncodeFlac(wav,flac,TestContext.Current.CancellationToken);paths.Add(flac);}
            foreach(string path in paths)
            {
                var samples=new List<object>();string? hash=null;AudioSession? session=null;
                samples.Add(await Measure("standalone SHA-256",()=>{hash=AtomicFileTransaction.Hash(path);return Task.CompletedTask;}));
                samples.Add(await Measure("open through coordinator",async()=>session=await files.Open(path)));
                var s=session!;
                samples.Add(await Measure("20 zoom/pan",async()=>{for(int i=0;i<20;i++){s.ShowRange(i*48000L,i*48000L+4800);await s.RefreshViewportAsync(cancellation:TestContext.Current.CancellationToken);}}));
                if(channels==2)samples.Add(await Measure("R +/-1 +/-10, invert, mix, reset",async()=>{foreach(int offset in new[]{1,-1,10,-10,0}){s.RightShift=offset.ToString(System.Globalization.CultureInfo.InvariantCulture);await s.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken);}s.InvertRight=true;await s.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken);s.Mix=true;await s.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken);s.ResetDraft();}));
                foreach(int seconds in new[]{10,60,1800})
                {
                    s.SetSelection([(0,48000L*seconds)]);
                    samples.Add(await Measure($"histogram {seconds}s",()=>s.MeasureAsync(TestContext.Current.CancellationToken)));
                }
                foreach(int seconds in new[]{10,1800})
                {
                    s.SetSelection([(0,48000L*seconds)]);
                    samples.Add(await Measure($"stage WAV {seconds}s",async()=>{using var stage=await AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken,"Stereo");}));
                }
                if(label=="after"&&bits==16&&channels==2&&path==wav)
                {
                    samples.Add(await Measure("20 overlapping preview requests",async()=>{var work=new List<Task>();for(int i=0;i<20;i++){s.ShowRange(i*48000L,i*48000L+4800);work.Add(s.RefreshViewportAsync(cancellation:TestContext.Current.CancellationToken));}await Task.WhenAll(work);}));
                    foreach(int seconds in new[]{10,60,1800})
                    {
                        s.SetSelection([(0,48000L*seconds)]);
                        samples.Add(await Measure($"deeper analysis {seconds}s (one detector/normal polarity)",async()=>{Assert.True(await s.RecoverAsync(new(){ZeroCrossing=true,Schmitt=false,Normal=true,Inverted=false,ThresholdScales=[1]},null,null,TestContext.Current.CancellationToken));}));
                    }
                    foreach(int seconds in new[]{10,1800})
                    {
                        s.SetSelection([(0,48000L*seconds)]);string destination=files.FilePath($"published-{seconds}.wav");
                        samples.Add(await Measure($"complete WAV export {seconds}s",async()=>{using var stage=await AudioOperations.StageWaveAsync(s,null,TestContext.Current.CancellationToken,"Stereo");await files.Coordinator.WriteExportAsync(new Dictionary<string,byte[]>{[destination]=[]},TestContext.Current.CancellationToken,new Dictionary<string,string>{[destination]=stage.Path},s.VerifySources);}));
                        if(seconds==1800)Assert.Equal(AtomicFileTransaction.Hash(wav),AtomicFileTransaction.Hash(destination));
                    }
                }
                Assert.Equal(hash,AtomicFileTransaction.Hash(path));await s.StopPreviewAsync();s.Dispose();
                results.Add(new {file=System.IO.Path.GetFileName(path),bytes=new FileInfo(path).Length,bits,channels,seconds=1800,cache="warm: fixture generated/encoded and hashed before open",samples});
                await File.WriteAllTextAsync(System.IO.Path.Combine(output,label+".json"),JsonSerializer.Serialize(new {label,platform=RuntimeInformation.OSDescription,framework=RuntimeInformation.FrameworkDescription,logicalIoIsNotPhysicalDiskIo=true,results},new JsonSerializerOptions{WriteIndented=true}),TestContext.Current.CancellationToken);
            }
        }
    }
    private static void WriteWave(string path,int bits,int channels)
    {
        const int frames=48000*1800;int align=channels*bits/8;using var writer=new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);writer.Write(36+frames*align);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)channels);writer.Write(48000);writer.Write(48000*align);writer.Write((short)align);writer.Write((short)bits);writer.Write("data"u8);writer.Write(frames*align);
        var buffer=new byte[48000*align];uint random=123456789;
        for(int second=0;second<1800;second++)
        {
            int at=0;for(int i=0;i<48000;i++)for(int c=0;c<channels;c++)
            {
                random^=random<<13;random^=random>>17;random^=random<<5;
                int value=(int)(random&1023)-512+((i%48<24)?8000:-8000);if(bits==24)value*=256;
                buffer[at++]=(byte)value;buffer[at++]=(byte)(value>>8);if(bits==24)buffer[at++]=(byte)(value>>16);
            }
            writer.Write(buffer);
        }
    }
}
