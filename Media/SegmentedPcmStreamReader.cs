using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace MZTools;

/// <summary>One continuous PCM timeline backed by the verified segments of a completed recorder run.</summary>
internal sealed class SegmentedPcmStreamReader : IPcmAudioStreamReader
{
    private sealed record Part(string Path,long First,long Frames,long Bytes,DateTime Modified,string Hash);
    private readonly List<Part> parts=[];
    private bool disposed;
    private static readonly object cacheGate=new();
    private static readonly Dictionary<(string Path,long Bytes,DateTime Modified,string Hash),bool> verified=[];
    internal static bool IsManifest(string path)=>path.EndsWith(".wav.json",StringComparison.OrdinalIgnoreCase);
    public PcmAudioFormat Format { get; }
    public string SourceFormat=>$"WAV segments ({parts.Count} files)";
    internal IReadOnlyList<string> SourceFiles=>parts.Select(p=>p.Path).ToArray();
    internal SegmentedPcmStreamReader(string manifest)
    {
        manifest=Path.GetFullPath(manifest);
        if(!IsManifest(manifest)) throw new ArgumentException("Select a recorder .wav.json report.");
        using var document=JsonDocument.Parse(File.ReadAllBytes(manifest));
        var root=document.RootElement;
        if(root.GetProperty("schema").GetString()!="mztools-live-capture-v1"||root.GetProperty("status").GetString()!="Complete")
            throw new InvalidDataException("Only a complete MZTools recording can be joined without assuming missing samples.");
        var recording=root.GetProperty("recording"); var output=recording.GetProperty("conversion").GetProperty("output");
        PcmAudioFormat? format=null; long total=0; int index=0;
        string target=manifest[..^5];
        foreach(var item in recording.GetProperty("segments").EnumerateArray())
        {
            string path=index==0?target:Path.Combine(Path.GetDirectoryName(target)!,Path.GetFileNameWithoutExtension(target)+"_"+(index+1).ToString("D3",System.Globalization.CultureInfo.InvariantCulture)+".wav");
            // Resolve only expected local segment names, also permitting relocation of the whole recording folder.
            if(!string.Equals(Path.GetFileName(item.GetProperty("path").GetString()),Path.GetFileName(path),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected segment name/order in recording report.");
            if(!item.GetProperty("published").GetBoolean()||!item.GetProperty("headerFinalized").GetBoolean()||item.GetProperty("pcmMatchesWrittenBytes").ValueKind!=JsonValueKind.True)
                throw new InvalidDataException("A recording segment is partial or unverified: "+path);
            long first=item.GetProperty("firstFrame").GetInt64(),frames=item.GetProperty("frames").GetInt64();
            if(first!=total||frames<=0) throw new InvalidDataException("Recording segments have a gap, overlap or empty interval: "+path);
            using var reader=new WavPcmStreamReader(path);
            var current=reader.Format; format??=current;
            if(current.Channels!=format.Value.Channels||current.SampleRate!=format.Value.SampleRate||current.BitsPerSample!=format.Value.BitsPerSample||current.BlockAlign!=format.Value.BlockAlign||current.FrameCount!=frames||current.DataLength!=item.GetProperty("dataBytes").GetInt64())
                throw new InvalidDataException("Recording segment format/length does not match the continuous stream: "+path);
            var file=new FileInfo(path); string hash=item.GetProperty("fileSha256").GetString()??"";
            if(file.Length!=item.GetProperty("fileBytes").GetInt64()||hash.Length!=64) throw new InvalidDataException("Recording segment size/hash is invalid: "+path);
            var part=new Part(path,first,frames,file.Length,file.LastWriteTimeUtc,hash);
            VerifyHash(part); parts.Add(part); total=checked(total+frames); index++;
        }
        if(format==null||total!=recording.GetProperty("savedFrames").GetInt64()||format.Value.Channels!=output.GetProperty("channels").GetInt32()||format.Value.SampleRate!=output.GetProperty("sampleRate").GetInt64()||format.Value.BitsPerSample!=output.GetProperty("bitsPerSample").GetInt32())
            throw new InvalidDataException("Recording report does not describe one complete compatible PCM stream.");
        Format=format.Value with { DataOffset=0,DataLength=checked(total*format.Value.BlockAlign) };
    }
    private static void VerifyHash(Part part)
    {
        var key=(part.Path,part.Bytes,part.Modified,part.Hash);
        lock(cacheGate)
        {
            if(verified.ContainsKey(key)) return;
            using var file=File.OpenRead(part.Path);
            if(!string.Equals(Convert.ToHexString(SHA256.HashData(file)),part.Hash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recording segment changed (SHA-256 mismatch): "+part.Path);
            if(verified.Count>=128) verified.Clear();
            verified[key]=true;
        }
    }
    public void ReadFrames(Action<long,int,int> consume)=>ReadFrames(0,Format.FrameCount,consume);
    public void ReadFrames(long firstFrame,long frameCount,Action<long,int,int> consume)
    {
        ObjectDisposedException.ThrowIf(disposed,this); ArgumentNullException.ThrowIfNull(consume);
        if(firstFrame<0||frameCount<0||firstFrame>Format.FrameCount-frameCount) throw new ArgumentOutOfRangeException(nameof(firstFrame));
        long end=checked(firstFrame+frameCount);
        foreach(var part in parts)
        {
            if(part.First>=end) break;
            if(part.First+part.Frames<=firstFrame) continue;
            using var reader=new WavPcmStreamReader(part.Path);
            var file=new FileInfo(part.Path);
            if(file.Length!=part.Bytes||file.LastWriteTimeUtc!=part.Modified) throw new IOException("Recording segment changed since analysis: "+part.Path);
            long start=Math.Max(firstFrame,part.First),last=Math.Min(end,part.First+part.Frames);
            reader.ReadFrames(start-part.First,last-start,(sample,left,right)=>consume(part.First+sample,left,right));
        }
    }
    public void Dispose()=>disposed=true;
}
