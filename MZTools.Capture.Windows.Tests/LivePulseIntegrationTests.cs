using System.Buffers.Binary;
using MZTools.Capture;

namespace MZTools.Capture.Windows.Tests;

public class LivePulseIntegrationTests
{
    [Fact]
    public void CatalogProjection_PreservesEveryHistoricalValueContextAndSource()
    {
        foreach(var reference in AudioPulseReferences.BuiltIn)
        {
            var projected=LivePulseCatalog.From(reference);
            Assert.Equal(reference.CatalogId,projected.Id); Assert.Equal(PulseReferenceCatalog.Version,projected.CatalogVersion);
            Assert.Equal(reference.Source,projected.Source); Assert.Equal(reference.Kind.ToString(),projected.Kind);
            Assert.Equal(reference.Guides.Count,projected.Guides.Count);
            for(int i=0;i<reference.Guides.Count;i++) { Assert.Equal(reference.Guides[i].Microseconds,projected.Guides[i].Microseconds); Assert.Equal(reference.Guides[i].Context,projected.Guides[i].Context); }
        }
    }
    [Theory]
    [InlineData(LivePulseDetector.ZeroCrossing)]
    [InlineData(LivePulseDetector.Hysteresis)]
    [InlineData(LivePulseDetector.Adaptive)]
    public async Task LiveAndExistingOfflineMeasurements_AgreeOnActualPcm(LivePulseDetector detector)
    {
        string folder=Path.Combine(Path.GetTempPath(),"mztools-live-pulse-parity-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            const int count=12000; var pcm=new byte[count*2];
            for(int i=0;i<count;i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i*2),(short)(.45*Math.Sin(i*.14)*32768+.08*Math.Sin(i*.4)*32768));
            string path=Path.Combine(folder,"parity.wav"); var format=new AudioCaptureFormat(48000,1,AudioSampleRepresentation.Signed16);
            using(var file=File.Create(path)) { file.Write(RiffWaveLayout.Header(format,count)); file.Write(pcm); }
            await using var live=new LiveSignalAnalyzer(); live.ConfigurePulses(new() { Reference=LivePulseCatalog.From(AudioPulseReferences.BuiltIn[0]) });
            await live.StartAsync(new(new("test","test","test",[]),format,format,"test"));
            for(int first=0;first<count;first+=137) { int n=Math.Min(137,count-first); live.Process(pcm.AsSpan(first*2,n*2),new(first,n,first)); }
            var measured=live.CreateSnapshot(new() { Pulses=new() { Detector=detector } })!.Pulses!;
            var offline=new List<(bool High,double Samples,long End)>();
            var mode=detector==LivePulseDetector.ZeroCrossing?WavPulseMode.ZeroCrossing:detector==LivePulseDetector.Hysteresis?WavPulseMode.Schmitt:WavPulseMode.AdaptiveZeroCrossing;
            using var reader=new WavPcmStreamReader(path);
            WavHeuristicAnalyzer.VisitRangeSignal(reader,0,count,new AudioPcmTransform(),1,(_,_,_)=>{},(_,m,high,duration,end)=>{ if(m==mode) offline.Add((high,duration,end)); },default,measureAdaptive:true);
            // Live excludes startup's unknown partial lobe and does not flush the unfinished final lobe.
            if(detector!=LivePulseDetector.Adaptive) offline.RemoveAt(0);
            var expected=offline.Where(p=>p.End<count-100).ToDictionary(p=>p.End);
            var comparable=measured.RecentPulses.Where(p=>p.EndFrame<count-100).ToArray(); Assert.True(comparable.Length>100);
            foreach(var pulse in comparable)
            { var original=expected[pulse.EndFrame]; Assert.Equal(original.High,pulse.PhysicalHigh); Assert.Equal(original.Samples,pulse.Microseconds*48000/1e6,8); }
        }
        finally { Directory.Delete(folder,recursive:true); }
    }
}
