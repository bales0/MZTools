using Avalonia.Controls;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Platform;

internal static class BatchProcessForm
{
    internal static readonly string[] ExtractionFormats=["Native","BIN","MZF","M12","MZT","WAV","FLAC","LEP","L16"];
    internal static bool ProducesAudio(BatchOperation operation,string target,string extract)=>operation==BatchOperation.ConvertFormat&&target is "WAV" or "FLAC"||operation==BatchOperation.ExtractAll&&extract is "WAV" or "FLAC";
    internal static bool SupportsAudioInput(BatchOperation operation,string target)=>BatchMediaPolicy.Supports(operation,"input.wav",target);
    internal static IReadOnlyList<DiskField> Fields()=>[
        new("operation","Operation","Analyze",Enum.GetNames<BatchOperation>()),new("input","Input selection","Choose files",["Choose files","Scan folder"]),
        new("folder","Input folder (for scan)",""),new("recursive","Include subfolders","No",["No","Yes"]),new("extensions","Folder extensions (recommended; editable restriction)",BatchMediaPolicy.Extensions(BatchOperation.Analyze)),
        new("output","Output directory",Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),new("target","Conversion target","HFEv3",BatchMediaPolicy.Targets.Distinct().ToArray()),new("template","Output name template","{name}_processed{ext}"),
        new("strict","Strict preflight","Yes",["Yes","No"]),new("replace","Replace originals (separate explicit confirmation follows)","No",["No","Yes"]),new("backup","Back up replaced originals","Yes",["Yes","No"]),
        new("auxiliary","Verified boot/system source (InstallBootSystem)",""),new("heuristic","Use heuristic analysis for WAV / FLAC inputs","No",["No","Yes"]),
        new("rate","WAV / FLAC output sample rate (Hz)","44100",["22050","44100"]),new("extract","Extract format","Native",ExtractionFormats)];
    internal static void Attach(IReadOnlyDictionary<string,Control> values,IReadOnlyDictionary<string,Control> rows)
    {
        string Selected(string key)=>(string?)((ComboBox)values[key]).SelectedItem??"";
        var extensions=(TextBox)values["extensions"];string? previousRecommendation=null;
        void Update()
        {
            var operation=Enum.Parse<BatchOperation>(Selected("operation"));string target=Selected("target");bool folder=Selected("input")=="Scan folder";
            string recommended=BatchMediaPolicy.Extensions(operation,target);
            if(previousRecommendation is null||string.IsNullOrWhiteSpace(extensions.Text)||extensions.Text==previousRecommendation)extensions.Text=recommended;
            previousRecommendation=recommended;ToolTip.SetTip(extensions,"This operation/target accepts: "+recommended+". A custom filter restricts folder scanning. Unsupported inputs are reported by Preview, never silently accepted.");
            void State(string key,bool visible){rows[key].IsVisible=visible;rows[key].IsEnabled=visible;}
            foreach(string key in new[]{"folder","recursive","extensions"})State(key,folder);
            bool writes=!BatchMediaPolicy.ReadOnly(operation);foreach(string key in new[]{"output","template"})State(key,writes);
            bool replace=writes&&operation!=BatchOperation.ExtractAll;State("replace",replace);if(!replace)((ComboBox)values["replace"]).SelectedItem="No";
            State("backup",replace&&Selected("replace")=="Yes");State("target",operation==BatchOperation.ConvertFormat);State("auxiliary",operation==BatchOperation.InstallBootSystem);State("extract",operation==BatchOperation.ExtractAll);
            State("heuristic",SupportsAudioInput(operation,target));State("rate",ProducesAudio(operation,target,Selected("extract")));
        }
        foreach(string key in new[]{"operation","input","target","extract","replace"})((ComboBox)values[key]).SelectionChanged+=(_,_)=>Update();Update();
    }
}
