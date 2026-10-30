using System.Text;
using System.Text.Json;

namespace MZTools.Desktop.Documents;

internal sealed class AudioWaveStage(string directory,string path,AudioSelectionWaveExport info) : IDisposable
{
    internal string Path {get;}=path;
    internal AudioSelectionWaveExport Info {get;}=info;
    public void Dispose()
    {
        string root=System.IO.Path.GetFullPath(directory),temp=System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if(!root.StartsWith(temp,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!=root)throw new IOException("Refusing cleanup outside owned audio staging directory.");
        if(File.Exists(Path))File.Delete(Path);if(Directory.Exists(root))Directory.Delete(root,false);
    }
}
internal static class AudioOperations
{
    internal static async Task<AudioWaveStage> StageWaveAsync(AudioSession session,IProgress<WavAnalysisProgress>? progress,CancellationToken ct,string? channels=null)
    {
        if(session.HasTransformDraft)throw new InvalidOperationException("Apply transform preview or Cancel edits before exporting.");
        if(session.SelectedIntervals.Count!=1)throw new InvalidOperationException("Choose one contiguous waveform interval for WAV export. Separate disjoint intervals are not concatenated silently.");
        long first=session.SelectionStart,last=session.SelectionEnd;var transform=session.EffectiveTransform;if(channels=="Average")transform=transform with{Mix=true};int mode=transform.Mix?0:session.ChannelMode==1?1:session.ChannelMode==2?2:0;
        if(channels is not null)mode=transform.Mix?0:channels=="Right"?1:channels=="Stereo"?2:0;
        string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"mztools-f6-wave-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string path=System.IO.Path.Combine(directory,"selected.wav");
        try
        {
            var result=await Task.Run(()=>
            {
                session.VerifySources();var info=AudioSelectionWaveExporter.Export(session.Model,path,first,last,transform,mode,progress,ct);
                using var reader=AudioSignalAnalysisService.OpenReader(path);
                if(reader.Format.FrameCount!=info.EndSample-info.StartSample||reader.Format.Channels!=info.Channels||reader.Format.BitsPerSample!=info.BitsPerSample||reader.Format.SampleRate!=info.SampleRate)throw new InvalidDataException("Staged WAV metadata differs from the selected PCM export.");
                session.VerifySources();return info;
            },ct);
            return new(directory,path,result);
        }
        catch
        {
            if(File.Exists(path))File.Delete(path);if(Directory.Exists(directory))Directory.Delete(directory,false);throw;
        }
    }
    internal static byte[] Report(AudioSession session,string format)=>format switch
    {
        "txt" or "csv" or "json"=>Encoding.UTF8.GetBytes(AudioAnalysisReport.Export(session.Model,format)),
        "recovery"=>session.Recovery is { } result?JsonSerializer.SerializeToUtf8Bytes(new{result.Source,result.StartSample,result.EndSample,programs=session.Programs.Select(p=>new{p.Name,p.Type,p.Load,p.Execute,p.Size,p.Profile}),candidates=session.Candidates.Select(c=>new{c.Index,c.Start,c.End,c.Bytes,c.Status,c.Channel,c.Detector}),passes=session.Passes,diagnostics=session.Diagnostics,rejectedHeaders=session.RejectedHeaders,failures=session.Failures,result.OmittedDiagnostics,result.OmittedHeaderRejections},new JsonSerializerOptions{WriteIndented=true}):throw new InvalidOperationException("Run deeper analysis first."),
        "comparison"=>session.PassComparison is { } comparison?JsonSerializer.SerializeToUtf8Bytes(comparison,new JsonSerializerOptions{WriteIndented=true}):throw new InvalidOperationException("Compare two retained passes first."),
        _=>throw new InvalidDataException("Unknown report format.")
    };
    internal static Task<ExportPreview> ProgramsAsync(AudioSession session,string path,string format,bool all,CancellationToken ct)
    {
        var records=session.ExportRecords(all);if(records.Length==0)throw new InvalidOperationException("No recovered programs are available.");
        var tape=TapeSession.New(false);tape.ApplyRecords(records);
        return MediaOperations.ExportAsync(tape,new(path,format,SelectedOnly:false,Separate:records.Length>1&&format is "Mzf" or "M12"),ct);
    }
}
