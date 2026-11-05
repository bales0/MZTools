using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace MZTools;

/// <summary>One continuous PCM timeline. Strict opens verify all segment bytes.
/// Metadata-only preview callers retain an analysis snapshot and must verify content separately before export.</summary>
internal sealed class SegmentedPcmStreamReader : IPcmAudioStreamReader
{
    private sealed record Part(string Path,long First,long Frames,long Bytes,DateTime Modified,string Hash);
    private readonly List<Part> parts=[];
    private bool disposed;
    internal static bool IsManifest(string path)=>path.EndsWith(".wav.json",StringComparison.OrdinalIgnoreCase);
    public PcmAudioFormat Format { get; }
    public string SourceFormat=>$"WAV segments ({parts.Count} files)";
    internal IReadOnlyList<string> SourceFiles=>parts.Select(p=>p.Path).ToArray();
    internal SegmentedPcmStreamReader(string manifest,bool verifyContent=true,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
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
            cancellation.ThrowIfCancellationRequested();
            if(verifyContent)VerifyHash(part,cancellation); parts.Add(part); total=checked(total+frames); index++;
        }
        if(format==null||total!=recording.GetProperty("savedFrames").GetInt64()||format.Value.Channels!=output.GetProperty("channels").GetInt32()||format.Value.SampleRate!=output.GetProperty("sampleRate").GetInt64()||format.Value.BitsPerSample!=output.GetProperty("bitsPerSample").GetInt32())
            throw new InvalidDataException("Recording report does not describe one complete compatible PCM stream.");
        Format=format.Value with { DataOffset=0,DataLength=checked(total*format.Value.BlockAlign) };
    }
    private static void VerifyHash(Part part,CancellationToken cancellation)
    {
        // Metadata is not a cryptographic cache key. Strict opens always verify bytes.
        if(!string.Equals(AtomicFileTransaction.HashCancellable(part.Path,cancellation),part.Hash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recording segment changed (SHA-256 mismatch): "+part.Path);
        var file=new FileInfo(part.Path);
        if(file.Length!=part.Bytes||file.LastWriteTimeUtc!=part.Modified)
            throw new IOException("Recording segment changed while verifying: "+part.Path);
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
