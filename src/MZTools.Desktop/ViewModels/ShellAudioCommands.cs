using System.Globalization;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    public AudioSession? ActiveAudio=>Documents.Active as AudioSession;
    public bool ShowAudio=>ActiveAudio is not null&&SelectedModule?.Name=="Audio analysis";
    public bool CanUseAudio=>ActiveAudio is not null&&!Documents.IsBusy;
    public AsyncCommand AudioSelectFramesCommand {get;private set;}=null!;
    public AsyncCommand AudioPreviewCommand {get;private set;}=null!;
    public AsyncCommand AudioResetCommand {get;private set;}=null!;
    public AsyncCommand AudioMeasureCommand {get;private set;}=null!;
    public AsyncCommand AudioSpectrumCommand {get;private set;}=null!;
    public AsyncCommand AudioSpectrogramCommand {get;private set;}=null!;
    public AsyncCommand AudioRecoverCommand {get;private set;}=null!;
    public AsyncCommand AudioWaveExportCommand {get;private set;}=null!;
    public AsyncCommand AudioReportExportCommand {get;private set;}=null!;
    public AsyncCommand AudioProgramsExportCommand {get;private set;}=null!;
    public AsyncCommand AudioCandidateExportCommand {get;private set;}=null!;
    public AsyncCommand AudioSyntheticCommand {get;private set;}=null!;
    public AsyncCommand AudioSendTapeCommand {get;private set;}=null!;
    public AsyncCommand AudioSendQuickDiskCommand {get;private set;}=null!;
    public AsyncCommand AudioComparePassesCommand {get;private set;}=null!;
    public AsyncCommand AudioReferenceCommand {get;private set;}=null!;
    public AsyncCommand AudioTriggerCommand {get;private set;}=null!;
    public AsyncCommand AudioCancelDetailCommand {get;private set;}=null!;
    private IDiskDialogs? AnalysisDialogs=>DiskDialogs;
    private void InitializeAudio()
    {
        AsyncCommand Op(Func<AudioSession,CancellationToken,Task<bool>> action,Func<bool>? allowed=null)=>new(async()=>{var audio=ActiveAudio;if(audio is not null)await Documents.ExecuteAsync(ct=>action(audio,ct));},()=>CanUseAudio&&(allowed?.Invoke()??true));
        bool Clean()=>ActiveAudio?.HasTransformDraft==false;
        AudioSelectFramesCommand=Op(async(s,_)=>
        {
            var a=await AnalysisDialogs!.DiskFormAsync("Select exact audio frames",[new("start","Start frame (inclusive)",s.SelectionStart.ToString(CultureInfo.InvariantCulture)),new("end","End frame (exclusive)",s.SelectionEnd.ToString(CultureInfo.InvariantCulture))],$"Source has {s.Model.FrameCount:N0} frames at {s.Model.SampleRate:N0} Hz. One frame contains all channels.");if(a is null)return false;
            long first=long.Parse(a["start"],CultureInfo.InvariantCulture),last=long.Parse(a["end"],CultureInfo.InvariantCulture);s.SetSelection([(first,last)]);return true;
        },()=>AnalysisDialogs is not null);
        AudioPreviewCommand=Op((s,ct)=>s.ApplyTransformPreviewAsync(ct));
        AudioResetCommand=Op((s,_)=>{s.ResetDraft();return Task.FromResult(true);});
        AudioMeasureCommand=Op((s,ct)=>s.MeasureAsync(ct),Clean);
        AudioSpectrumCommand=Op((s,ct)=>s.ComputeSpectralAsync(false,Documents.AudioProgress(),ct),Clean);
        AudioSpectrogramCommand=Op((s,ct)=>s.ComputeSpectralAsync(true,Documents.AudioProgress(),ct),Clean);
        AudioRecoverCommand=Op(RunRecoveryAsync,()=>AnalysisDialogs is not null);
        AudioWaveExportCommand=Op(async(s,ct)=>
        {
            if(AnalysisDialogs is null)return false;
            var a=await AnalysisDialogs.DiskFormAsync("Export selected waveform",[new("scope","Channels",s.ChannelMode==2?"Stereo":s.ChannelMode==1?"Right":"Left / mono",s.Model.ChannelCount==1?["Left / mono"]:["Left / mono","Right","Stereo","Average"])],s.SelectionSummary+"\n"+s.TransformDescription+"\nWAV contains actual selected PCM, not waveform buckets. The applied alignment/mix and original supported sample rate/bit depth are retained.");if(a is null)return false;
            using var stage=await AudioOperations.StageWaveAsync(s,Documents.AudioProgress(),ct,a["scope"]);
            if(!await AnalysisDialogs.ConfirmDiskAsync("WAV export preview",$"Frames [{stage.Info.StartSample:N0}, {stage.Info.EndSample:N0}) · {stage.Info.Channels} ch · {stage.Info.SampleRate:N0} Hz / PCM {stage.Info.BitsPerSample} bit\nThe source set remains unchanged. Output is verified before replacement."))return false;
            var path=await AnalysisDialogs.PickDiskOutputAsync("Export selected PCM","selection.wav");if(path is null)return false;
            if(!System.IO.Path.GetExtension(path).Equals(".wav",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Choose a .wav destination for the PCM export.");
            await Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=[]},ct,new Dictionary<string,string>{[path]=stage.Path},s.VerifySources);return true;
        },()=>AnalysisDialogs is not null&&Clean());
        AudioReportExportCommand=Op(async(s,ct)=>
        {
            var a=await AnalysisDialogs!.DiskFormAsync("Export analysis report",[new("format","Content / format","txt",["txt","csv","json","recovery","comparison"])],"Analysis report formats use the existing Core exporter. Recovery and comparison exports contain retained evidence; they do not change program validity.");if(a is null)return false;
            var data=AudioOperations.Report(s,a["format"]);var path=await AnalysisDialogs.PickDiskOutputAsync("Export analysis report","analysis."+(a["format"] is "recovery" or "comparison"?"json":a["format"]));if(path is null)return false;
            await Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=data},ct,beforeCommit:s.VerifySources);return true;
        },()=>AnalysisDialogs is not null);
        AudioProgramsExportCommand=Op(async(s,ct)=>
        {
            var a=await AnalysisDialogs!.DiskFormAsync("Export recovered programs",[new("scope","Programs","Selected",["Selected","All"]),new("format","Tape format","Mzt",["Mzt","Mzf","M12"])],"Only complete Core-recovered programs are listed here. MZF/M12 export multiple records as separate files; MZT keeps the collection and profile metadata.");if(a is null)return false;
            string extension=a["format"]=="Mzt"?".mzt":a["format"]=="Mzf"?".mzf":".m12";
            var path=await AnalysisDialogs.PickDiskOutputAsync("Export programs","recovered"+extension);if(path is null)return false;
            if(!System.IO.Path.GetExtension(path).Equals(extension,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The selected tape format does not match the destination extension.");
            using var plan=await AudioOperations.ProgramsAsync(s,path,a["format"],a["scope"]=="All",ct);
            if(!await AnalysisDialogs.ConfirmDiskAsync("Recovered program export preview",plan.Report))return false;
            await Documents.WriteExportAsync(plan.Outputs,ct,plan.SourceFiles,s.VerifySources);if(a["scope"]=="All"||s.Programs.Count==1)s.MarkExported();return true;
        },()=>AnalysisDialogs is not null&&ActiveAudio?.Programs.Count>0);
        AudioCandidateExportCommand=Op(async(s,ct)=>
        {
            var row=s.SelectedCandidate!;if(!await AnalysisDialogs!.ConfirmDiskAsync("Export raw candidate",row.Status+"\n"+row.Value.BoundaryEvidence+$"\n{row.Bytes:N0} raw decoded bytes; no recovered tape header is claimed."))return false;
            var path=await AnalysisDialogs.PickDiskOutputAsync("Export raw candidate","candidate.bin");if(path is null)return false;
            await Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=row.Value.Data.ToArray()},ct,beforeCommit:s.VerifySources);return true;
        },()=>AnalysisDialogs is not null&&ActiveAudio?.SelectedCandidate is not null);
        AudioSyntheticCommand=Op(async(s,ct)=>
        {
            var a=await AnalysisDialogs!.DiskFormAsync("Synthetic MZF header",[new("name","Sharp filename (1–16 characters)","RECOVERED"),new("type","Type (hex)","01"),new("load","LOAD (hex)","4000"),new("exec","EXEC (hex)","4000"),new("description","Description","User supplied metadata"),new("ack","I supply this synthetic metadata; it was not recovered from the original header","No",["No","Yes"])],s.SelectedCandidate!.Status+"\nName/type/LOAD/EXEC cannot be inferred from this payload. The original header is unavailable.");if(a is null)return false;if(a["ack"]!="Yes")throw new InvalidOperationException("Acknowledge the synthetic metadata explicitly.");
            var record=s.CreateSynthetic(a["name"],checked((byte)DiskSession.Hex16(a["type"])),DiskSession.Hex16(a["load"]),DiskSession.Hex16(a["exec"]),a["description"]);
            if(!await AnalysisDialogs.ConfirmDiskAsync("Create synthetic tape record",$"{record.Body.DataSize:N0} checksum-matching payload bytes with a user-defined synthetic header. No original header recovery or program execution is claimed."))return false;
            ct.ThrowIfCancellationRequested();s.VerifySources();var tape=TapeSession.New(false);tape.ApplyRecords([record]);Documents.Add(tape);return true;
        },()=>AnalysisDialogs is not null&&ActiveAudio?.SelectedCandidate?.CanSynthesize==true);
        async Task<bool> Send(AudioSession s,bool quick,CancellationToken ct)
        {
            var a=await AnalysisDialogs!.DiskFormAsync("Send recovered programs",[new("scope","Programs","Selected",["Selected","All"])],"Create a new Tape/QuickDisk document using complete recovered records. Audio source bytes are not changed.");if(a is null)return false;var records=s.ExportRecords(a["scope"]=="All");if(records.Length==0)throw new InvalidOperationException("No recovered programs.");
            ct.ThrowIfCancellationRequested();s.VerifySources();var tape=TapeSession.New(quick);tape.ApplyRecords(records);Documents.Add(tape);if(a["scope"]=="All"||s.Programs.Count==1)s.MarkExported();return true;
        }
        AudioSendTapeCommand=Op((s,ct)=>Send(s,false,ct),()=>AnalysisDialogs is not null&&ActiveAudio?.Programs.Count>0);
        AudioSendQuickDiskCommand=Op((s,ct)=>Send(s,true,ct),()=>AnalysisDialogs is not null&&ActiveAudio?.Programs.Count>0);
        AudioComparePassesCommand=Op(async(s,ct)=>{await s.ComparePassesAsync(ct);return true;},()=>ActiveAudio?.Passes.Count>0);
        AudioReferenceCommand=Op(async(s,_)=>
        {
            var a=await AnalysisDialogs!.DiskFormAsync("Session-local pulse reference",[new("name","Reference name","My reference"),new("sh","SHORT High (µs)","200"),new("sl","SHORT Low (µs)","200"),new("lh","LONG High (µs)","400"),new("ll","LONG Low (µs)","400")],"User-entered comparison values, not an independent measurement. The central built-in timing catalog remains unchanged.");if(a is null)return false;
            double Number(string key)=>double.Parse(a[key],CultureInfo.InvariantCulture);s.AddReference(a["name"],Number("sh"),Number("sl"),Number("lh"),Number("ll"));return true;
        },()=>AnalysisDialogs is not null);
        AudioTriggerCommand=Op(async(s,ct)=>
        {
            var a=await AnalysisDialogs!.DiskFormAsync("Offline waveform trigger",[new("level","Trigger level (FS)","0"),new("edge","Edge","Rising",["Rising","Falling"])],"Search bounded actual applied PCM around the viewport for the latest centered crossing. At most two seconds are retained around the crossing; no samples or timestamps are moved.");if(a is null)return false;
            return await s.TriggerAsync(double.Parse(a["level"],CultureInfo.InvariantCulture),a["edge"]=="Rising",ct);
        },()=>AnalysisDialogs is not null&&Clean());
        AudioCancelDetailCommand=new(()=>{ActiveAudio?.CancelViewport();return Task.CompletedTask;},()=>ActiveAudio?.IsPreviewBusy==true);
    }
    private void RefreshAudioCommands()
    {
        foreach(var c in new[]{AudioSelectFramesCommand,AudioPreviewCommand,AudioResetCommand,AudioMeasureCommand,AudioSpectrumCommand,AudioSpectrogramCommand,AudioRecoverCommand,AudioWaveExportCommand,AudioReportExportCommand,AudioProgramsExportCommand,AudioCandidateExportCommand,AudioSyntheticCommand,AudioSendTapeCommand,AudioSendQuickDiskCommand,AudioComparePassesCommand,AudioReferenceCommand,AudioTriggerCommand,AudioCancelDetailCommand})c.Refresh();
        Changed(nameof(ActiveAudio));Changed(nameof(ShowAudio));Changed(nameof(ShowReadiness));Changed(nameof(CanUseAudio));
    }
}
