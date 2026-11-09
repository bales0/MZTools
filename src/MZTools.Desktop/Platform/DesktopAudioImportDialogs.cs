using Avalonia.Controls;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Platform;

public sealed partial class DesktopDialogs : IAudioImportDialogs
{
    public Task<AudioImportChoice?> ChooseAudioImportAsync(IReadOnlyList<string> paths,bool manifestOnly)
    {
        var heuristic=new CheckBox{Content="Use heuristic analysis / recovery",IsChecked=manifestOnly,IsEnabled=!manifestOnly};
        var report=Box(new[]{"Summary","Detailed","Do not show"});
        return FormAsync("Audio import options",[new TextBlock{Text=$"Decode programs from {paths.Count} audio / recording file(s). Sources remain unchanged.",TextWrapping=Avalonia.Media.TextWrapping.Wrap},heuristic,
            new TextBlock{Text=manifestOnly?"Complete recordings use heuristic analysis.":"Standard is the default. Heuristic recovery supports noisy, offset, inverted and stereo recordings.",TextWrapping=Avalonia.Media.TextWrapping.Wrap},Label("Report after import",report)],
            ()=>new AudioImportChoice(heuristic.IsChecked==true?ProgramImportMode.Heuristic:ProgramImportMode.Standard,(ProgramImportReport)report.SelectedIndex));
    }
    public async Task ShowAudioImportReportAsync(string report)
    {await ConfirmAsync("Audio program import report",report,("Close",true));}
}
