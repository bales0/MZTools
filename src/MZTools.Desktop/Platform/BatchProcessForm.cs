using Avalonia.Controls;

namespace MZTools.Desktop.Platform;

internal static class BatchProcessForm
{
    internal static void Attach(IReadOnlyDictionary<string,Control> values,IReadOnlyDictionary<string,Control> rows)
    {
        string Selected(string key)=>(string?)((ComboBox)values[key]).SelectedItem??"";
        void Update()
        {
            string operation=Selected("operation");bool folder=Selected("input")=="Scan folder";
            foreach(string key in new[]{"folder","recursive","extensions"})rows[key].IsVisible=folder;
            bool writes=operation is not ("Analyze" or "TestIntegrity" or "AnalyzeMzfCompatibility");
            foreach(string key in new[]{"output","template"})rows[key].IsVisible=writes;
            bool replace=writes&&operation!="ExtractAll";
            rows["replace"].IsVisible=replace;if(!replace)((ComboBox)values["replace"]).SelectedItem="No";
            rows["backup"].IsVisible=replace&&Selected("replace")=="Yes";
            rows["target"].IsVisible=operation=="ConvertFormat";
            rows["auxiliary"].IsVisible=operation=="InstallBootSystem";
            rows["extract"].IsVisible=operation=="ExtractAll";
            rows["heuristic"].IsVisible=operation is "Analyze" or "ConvertFormat";
            rows["rate"].IsVisible=operation=="ConvertFormat"&&Selected("target").Equals("Wav",StringComparison.OrdinalIgnoreCase);
        }
        foreach(string key in new[]{"operation","input","target","replace"})((ComboBox)values[key]).SelectionChanged+=(_,_)=>Update();
        Update();
    }
}
