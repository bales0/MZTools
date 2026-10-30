using System.Buffers.Binary;
using System.Text.Json;
namespace MZTools.Capture.Tests;

public sealed class SpectralReferenceTests
{
    [Theory]
    [InlineData("silence",1)][InlineData("dc",1)][InlineData("tone",1)][InlineData("off-bin",1)][InlineData("nyquist",1)][InlineData("clipped",1)]
    [InlineData("silence",2)][InlineData("dc",2)][InlineData("tone",2)][InlineData("off-bin",2)][InlineData("nyquist",2)][InlineData("clipped",2)]
    public async Task FrozenBeforeExtractionSpectrumAndTrigger(string kind,int channels)
    {
        const int n=4096;var format=new AudioCaptureFormat(48000,channels,AudioSampleRepresentation.Float32);
        await using var analyzer=new LiveSignalAnalyzer();await analyzer.StartAsync(new(new("synthetic","synthetic","synthetic",[]),format,format,"synthetic"));
        var data=new byte[n*channels*4];
        for(int i=0;i<n;i++)for(int ch=0;ch<channels;ch++)
        {
            double value=kind switch{"silence"=>0,"dc"=>.125,"nyquist"=>i%2==0?.5:-.5,"tone"=>.5*Math.Sin(2*Math.PI*128*i/n),"off-bin"=>.3*Math.Sin(2*Math.PI*128.37*i/n),_=>Math.Clamp(2*Math.Sin(2*Math.PI*128*i/n),-1,1)};
            if(ch==1)value=-value/2;BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan((i*channels+ch)*4),(float)value);
        }
        analyzer.Process(data,new(0,n,0));var snapshot=analyzer.CreateSnapshot(new(){FftSize=n,WaveSeconds=.02,TriggerEnabled=true})!;
        Assert.Equal(n/2+1,snapshot.Spectrum.Count);Assert.All(snapshot.Spectrum,b=>Assert.True(double.IsFinite(b.LeftDbfs)&&double.IsFinite(b.RightDbfs)));
        var output=Environment.GetEnvironmentVariable("MZTOOLS_F6_FFT_OUTPUT");if(output is null)return;Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,kind+"-"+channels+".json"),JsonSerializer.Serialize(new{snapshot.Spectrum,snapshot.LeftDominantHz,snapshot.RightDominantHz,snapshot.Triggered,snapshot.WaveStartFrame,snapshot.WaveEndFrame,snapshot.SpectrumStartFrame,snapshot.SpectrumEndFrame}));
    }
}
