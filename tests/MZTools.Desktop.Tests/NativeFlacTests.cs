using System.Security.Cryptography;
using MZTools.Platform.Audio;

namespace MZTools.Desktop.Tests;

public sealed class NativeFlacTests
{
    [Theory][InlineData(8,1)][InlineData(8,2)][InlineData(16,1)][InlineData(16,2)][InlineData(24,1)][InlineData(24,2)]
    public void SystemFlacRoundtripPreservesEveryIntegerPcmBitAndSeek(int bits,int channels)
    {
        using var files=new AudioDocumentTests();NativeAudioCodecs.Configure();string input=files.FilePath($"Příliš žluťoučký-{bits}-{channels}.wav"),output=files.FilePath("výsledek.flac");int[] values=[-8388608,-8388352,-4194304,-65536,-256,0,256,65536,4194304,8388352];const int frames=1031;
        using(var writer=new BinaryWriter(File.Create(input)))
        {
            int bytes=frames*channels*bits/8;writer.Write("RIFF"u8);writer.Write(36+bytes);writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)channels);writer.Write(48000);writer.Write(48000*channels*bits/8);writer.Write((short)(channels*bits/8));writer.Write((short)bits);writer.Write("data"u8);writer.Write(bytes);
            for(int i=0;i<frames;i++)for(int ch=0;ch<channels;ch++){int value=values[(i+ch)%values.Length];if(bits==8)writer.Write((byte)((value>>16)+128));else if(bits==16)writer.Write((short)(value>>8));else{writer.Write((byte)value);writer.Write((byte)(value>>8));writer.Write((byte)(value>>16));}}
        }
        CorePlatformServices.EncodeFlac(input,output,TestContext.Current.CancellationToken);using var a=new WavPcmStreamReader(input);using var b=CorePlatformServices.OpenFlac(output);Assert.Equal(a.Format.FrameCount,b.Format.FrameCount);Assert.Equal(a.Format.BitsPerSample,b.Format.BitsPerSample);var expected=new List<(long,int,int)>();var actual=new List<(long,int,int)>();a.ReadFrames((i,l,r)=>expected.Add((i,l,r)));b.ReadFrames((i,l,r)=>actual.Add((i,l,r)));Assert.Equal(expected,actual);actual.Clear();b.ReadFrames(17,33,(i,l,r)=>actual.Add((i,l,r)));Assert.Equal(expected.Skip(17).Take(33),actual);Assert.Contains("libsndfile",NativeAudioCodecs.Version,StringComparison.OrdinalIgnoreCase);
        if(Environment.GetEnvironmentVariable("MZTOOLS_COMPLETION_OUTPUT") is { } root){Directory.CreateDirectory(root);File.WriteAllBytes(Path.Combine(root,$"flac-{bits}-{channels}.wav"),File.ReadAllBytes(input));File.WriteAllText(Path.Combine(root,$"flac-{bits}-{channels}.sha256"),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))));}
    }
    [Fact]public void CanceledFlacEncodePublishesNothingAndPreservesSource()
    {
        using var files=new AudioDocumentTests();NativeAudioCodecs.Configure();string input=files.Wave(),output=files.FilePath("cancel.flac");var bytes=File.ReadAllBytes(input);using var canceled=new CancellationTokenSource();canceled.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>CorePlatformServices.EncodeFlac(input,output,canceled.Token));Assert.False(File.Exists(output));Assert.Equal(bytes,File.ReadAllBytes(input));Assert.Empty(Directory.GetFiles(files.Root,"*.encoding-*"));
    }
}
