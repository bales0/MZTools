namespace MZTools.Desktop.Documents;

public enum ProgramImportMode { Standard, Heuristic }
public enum ProgramImportReport { Summary, Detailed, None }
public sealed record AudioImportChoice(ProgramImportMode Mode = ProgramImportMode.Standard,
    ProgramImportReport Report = ProgramImportReport.Summary);
public interface IAudioImportDialogs
{
    Task<AudioImportChoice?> ChooseAudioImportAsync(IReadOnlyList<string> paths, bool manifestOnly);
    Task ShowAudioImportReportAsync(string report);
}
internal sealed record ProgramImportResult(IReadOnlyList<TapeRecord> Records, Dictionary<string,string?> Sources, string Report);

// Orchestration only: the original Core decoders, timing and fallback policy are reused.
internal static class AudioProgramImport
{
    internal static bool IsSignal(string path) => AudioSession.IsSource(path) ||
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".lep" or ".l16";
    internal static async Task<AudioImportChoice> ChooseAsync(IEnumerable<string> paths, IAudioImportDialogs? dialogs, CancellationToken ct)
    {
        var audio=paths.Where(AudioSession.IsSource).ToArray();
        bool manifestOnly=audio.Length>0&&audio.All(SegmentedPcmStreamReader.IsManifest);
        ct.ThrowIfCancellationRequested();
        if(audio.Length==0)return new();
        var choice=dialogs is null?new(manifestOnly?ProgramImportMode.Heuristic:ProgramImportMode.Standard):await dialogs.ChooseAudioImportAsync(audio,manifestOnly);
        ct.ThrowIfCancellationRequested();
        return choice??throw new OperationCanceledException(ct);
    }
    internal static async Task<ProgramImportResult> ReadAsync(string path,AudioImportChoice choice,IProgress<WavAnalysisProgress>? progress,CancellationToken ct)
    {
        if(System.IO.Path.GetExtension(path).Equals(".flac",StringComparison.OrdinalIgnoreCase))MZTools.Platform.Audio.NativeAudioCodecs.Configure();
        return await Task.Run(()=>
        {
            ct.ThrowIfCancellationRequested();
            string[] sources=[System.IO.Path.GetFullPath(path)];
            if(SegmentedPcmStreamReader.IsManifest(path))
            {
                using var reader=new SegmentedPcmStreamReader(path,verifyContent:false,cancellation:ct);
                sources=sources.Concat(reader.SourceFiles).Distinct(AtomicFileTransaction.PathComparer).ToArray();
            }
            var hashes=sources.ToDictionary(p=>p,p=>AtomicFileTransaction.HashCancellable(p,ct)??throw new FileNotFoundException("Import source missing.",p),AtomicFileTransaction.PathComparer);
            WavHeuristicAnalysisResult? analysis=null;bool fallback=false;
            IReadOnlyList<TapeRecord> records;
            bool manifest=SegmentedPcmStreamReader.IsManifest(path);
            if(manifest||choice.Mode==ProgramImportMode.Heuristic&&AudioSession.IsSource(path))
            {
                analysis=WavHeuristicAnalyzer.AnalyzeFile(path,progress,ct);
                records=analysis.Records;
                if(!manifest&&AudioImportPolicy.ShouldTryStandardDecoder(analysis))
                {
                    try{var standard=SharpTapeImporter.ReadFile(path,ct);if(standard.Count>records.Count){records=standard;fallback=true;}}
                    catch(OperationCanceledException){throw;}
                    catch(Exception error)when(error is InvalidDataException or ArgumentException or IOException)
                    {if(records.Count==0)throw new InvalidDataException("Heuristic recovery and standard fallback failed: "+error.Message,error);}
                }
            }
            else records=SharpTapeImporter.ReadFile(path,ct);
            if(records.Count==0)throw new InvalidDataException("No checksum-valid program recovered from "+path+(analysis is null?"":"\nHeader candidates: "+analysis.Statistics.HeaderCandidates+"; payload candidates: "+analysis.Statistics.PayloadCandidates+"; failures: "+analysis.Failures.Count+"\n"+string.Join("\n",analysis.Failures.Select(f=>f.Reason))));
            foreach(var pair in hashes)if(AtomicFileTransaction.HashCancellable(pair.Key,ct)!=pair.Value)throw new IOException("Import source changed: "+pair.Key);
            string report=$"{System.IO.Path.GetFileName(path)}: {records.Count} program(s), {records.Sum(r=>(long)r.Body.DataSize)} payload bytes. Mode: {(manifest?"Heuristic (complete recording)":choice.Mode.ToString())}.";
            if(analysis is not null)
            {
                report+=$"\nHeader candidates: {analysis.Statistics.HeaderCandidates}; payload candidates: {analysis.Statistics.PayloadCandidates}; valid: {analysis.Statistics.ValidPayloadCandidates}; failures: {analysis.Failures.Count}.";
                if(choice.Report==ProgramImportReport.Detailed)report+="\n"+System.Text.Json.JsonSerializer.Serialize(analysis.Statistics.GetType().GetProperties(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).Where(p=>p.GetIndexParameters().Length==0).ToDictionary(p=>p.Name,p=>p.GetValue(analysis.Statistics)),new System.Text.Json.JsonSerializerOptions{WriteIndented=true})+"\n"+string.Join("\n",analysis.Failures.Select(f=>f.Reason));
            }
            if(fallback)report+="\nHeuristic result incomplete; checksum-valid standard decoder recovered more records and was used.";
            report+="\nSource audio/pulses and metadata are unchanged. Save As creates a tape container.";
            return new ProgramImportResult(records,hashes.ToDictionary(p=>p.Key,p=>(string?)p.Value,AtomicFileTransaction.PathComparer),report);
        },ct);
    }
    internal static void Verify(IReadOnlyDictionary<string,string?> sources,CancellationToken ct)
    {foreach(var pair in sources)if(AtomicFileTransaction.HashCancellable(pair.Key,ct)!=pair.Value)throw new IOException("Import source changed before Apply: "+pair.Key);}
}
