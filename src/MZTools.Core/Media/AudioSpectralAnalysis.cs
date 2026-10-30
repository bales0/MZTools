using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MZTools.Capture;

namespace MZTools;

internal sealed record AudioSpectrum(long StartFrame,long EndFrame,uint SampleRate,int FftSize,
    IReadOnlyList<LiveSpectrumBin> Bins,double? LeftPeak,double? RightPeak)
{
    internal string Method=>"Periodic Hann; coherent-gain corrected amplitude dBFS, not PSD; floor −160 dBFS";
}
internal sealed record AudioSpectralColumn(long StartFrame,long EndFrame,int Windows,float[] Left,float[] Right);
internal sealed record AudioSpectrogram(long StartFrame,long EndFrame,uint SampleRate,int FftSize,int Hop,
    int FrequencyGroup,IReadOnlyList<AudioSpectralColumn> Columns,long Windows,long UncoveredTail)
{
    internal long Bytes=>Columns.Sum(c=>(long)(c.Left.Length+c.Right.Length)*sizeof(float));
    internal string Method=>"Max amplitude over declared FFT windows/frequency bins; no interpolation. Periodic Hann dBFS, not PSD.";
}
internal static class AudioSpectralAnalysis
{
    private static void Validate(int n)
    {if(n is <256 or >32768||(n&(n-1))!=0)throw new ArgumentOutOfRangeException(nameof(n),"FFT N must be a power of two, 256–32768.");}
    internal static AudioSpectrum Spectrum(AudioSignalAnalysis source,long start,long end,AudioPcmTransform transform,int n,CancellationToken ct)
    {
        Validate(n);AudioRangeInspection.VerifySource(source);using var reader=AudioSignalAnalysisService.OpenReader(source.Source);
        (start,end)=transform.Bounds(reader.Format,start,end);long first=Math.Max(start,end-n);
        if(end-first<n)return new(first,end,source.SampleRate,n,[],null,null);
        int channels=transform.Mix?1:reader.Format.Channels;var data=Enumerable.Range(0,channels).Select(_=>new double[n]).ToArray();int index=0;
        transform.Visit(reader,first,end,(_,left,right)=>{data[0][index]=left/8388608.0;if(channels==2)data[1][index]=right/8388608.0;index++;},ct);
        if(index!=n)throw new IOException("The selected spectrum window is incomplete.");
        var bins=new SpectralAnalysis().Compute(data,n,n,source.SampleRate,channels,out var l,out var r);
        AudioRangeInspection.VerifySource(source);return new(first,end,source.SampleRate,n,bins,l,r);
    }
    internal static AudioSpectrogram Spectrogram(AudioSignalAnalysis source,long start,long end,AudioPcmTransform transform,int n,
        IProgress<WavAnalysisProgress>? progress,CancellationToken ct,int maximumColumns=128,int maximumFrequencyBins=1024)
    {
        Validate(n);if(maximumColumns is <1 or >256||maximumFrequencyBins is <1 or >2048)throw new ArgumentOutOfRangeException(nameof(maximumColumns));
        AudioRangeInspection.VerifySource(source);using var reader=AudioSignalAnalysisService.OpenReader(source.Source);(start,end)=transform.Bounds(reader.Format,start,end);
        int hop=n/2,channels=transform.Mix?1:reader.Format.Channels;long windows=end-start<n?0:1+(end-start-n)/hop;
        long perColumn=Math.Max(1,(windows+maximumColumns-1)/maximumColumns);int frequencyGroup=(n/2+1+maximumFrequencyBins-1)/maximumFrequencyBins,bins=(n/2+1+frequencyGroup-1)/frequencyGroup;
        var columns=new List<AudioSpectralColumn>();var data=Enumerable.Range(0,channels).Select(_=>new double[n]).ToArray();var spectral=new SpectralAnalysis();int filled=0;long processed=0,lastEnd=start;
        if(windows==0)return new(start,end,source.SampleRate,n,hop,frequencyGroup,[],0,end-start);
        transform.Visit(reader,start,end,(frame,left,right)=>
        {
            data[0][filled]=left/8388608.0;if(channels==2)data[1][filled]=right/8388608.0;filled++;
            if(filled<n)return;
            ct.ThrowIfCancellationRequested();var values=spectral.Compute(data,n,n,source.SampleRate,channels,out _,out _);long windowStart=frame-n+1;
            if(processed%perColumn==0)columns.Add(new(windowStart,frame+1,0,Enumerable.Repeat(-160f,bins).ToArray(),Enumerable.Repeat(-160f,bins).ToArray()));
            var column=columns[^1];for(int i=0;i<values.Length;i++){int bin=i/frequencyGroup;column.Left[bin]=Math.Max(column.Left[bin],(float)values[i].LeftDbfs);column.Right[bin]=Math.Max(column.Right[bin],(float)values[i].RightDbfs);}
            columns[^1]=column with{EndFrame=frame+1,Windows=column.Windows+1};processed++;lastEnd=frame+1;
            foreach(var channel in data)Array.Copy(channel,hop,channel,0,n-hop);filled=n-hop;
            if(processed%16==0)progress?.Report(new("Spectrogram FFT windows",processed/(double)windows,processed,windows,0));
        },ct);
        if(processed!=windows)throw new IOException("The spectral interval was incomplete.");AudioRangeInspection.VerifySource(source);
        return new(start,end,source.SampleRate,n,hop,frequencyGroup,columns,processed,end-lastEnd);
    }
    internal static (long Start,long End,bool Triggered) Trigger(AudioSignalAnalysis source,long start,long end,AudioPcmTransform transform,int channel,double level,bool rising,CancellationToken ct)
    {
        if(channel<0||channel>=source.ChannelCount||end<=start)throw new ArgumentOutOfRangeException(nameof(channel));
        AudioRangeInspection.VerifySource(source);using var reader=AudioSignalAnalysisService.OpenReader(source.Source);
        (start,end)=transform.Bounds(reader.Format,start,end);
        int requested=checked((int)Math.Min(end-start,source.SampleRate*2L));var full=transform.Bounds(reader.Format,0,reader.Format.FrameCount);long scanEnd=Math.Min(full.End,end+requested/2L),first=Math.Max(full.Start,scanEnd-2L*requested);int count=checked((int)(scanEnd-first));var data=new double[count];int index=0;
        transform.Visit(reader,first,scanEnd,(_,left,right)=>{ct.ThrowIfCancellationRequested();data[index++]=(channel==1?right:left)/8388608.0;},ct);
        if(index!=count)throw new IOException("Incomplete trigger interval.");AudioRangeInspection.VerifySource(source);int? crossing=SpectralAnalysis.FindCenteredCrossing(data,requested,level,rising);if(crossing is null)return(start,end,false);
        long from=first+crossing.Value-Math.Min(requested,count)/2;return(from,Math.Min(scanEnd,from+requested),true);
    }
}
