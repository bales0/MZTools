using System.Diagnostics;
using MZTools.Capture;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Platform;

internal sealed class DesktopRecorderNavigation(MainWindow? initial) : IRecorderNavigation
{
    private MainWindow? browser=initial;
    public async Task OpenAudioAsync(string path,RecordedAudioAction action,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();if(browser?.IsVisible!=true){browser=new MainWindow();browser.Show();}browser.Activate();
        if(!await browser.Model.Documents.OpenPathAsync(path))throw new IOException(browser.Model.Documents.Error??"Audio navigation was canceled or the browser is busy.");
        if(browser.Model.ActiveAudio is not { } audio)throw new InvalidOperationException("The saved recording did not open as an audio document.");
        if(action==RecordedAudioAction.ManualRecovery)audio.ViewIndex=5;
        if(action==RecordedAudioAction.HeuristicImport)
        {
            var records=audio.ExportRecords(true);if(records.Length==0){audio.ViewIndex=5;throw new InvalidDataException("No complete program was recovered. Use manual recovery; the source remains read-only.");}
            var tape=TapeSession.New(false);tape.ApplyRecords(records);browser.Model.Documents.Add(tape);
        }
    }
    public void RevealFile(string path)
    {
        path=Path.GetFullPath(path);if(!File.Exists(path))throw new FileNotFoundException("Saved recording no longer exists.",path);
        var info=new ProcessStartInfo{UseShellExecute=false};
        if(OperatingSystem.IsWindows()){info.FileName="explorer.exe";info.ArgumentList.Add("/select,"+path);}
        else if(OperatingSystem.IsMacOS()){info.FileName="open";info.ArgumentList.Add("-R");info.ArgumentList.Add(path);}
        else{info.FileName="xdg-open";info.ArgumentList.Add(Path.GetDirectoryName(path)!);}
        Process.Start(info)?.Dispose();
    }
}
