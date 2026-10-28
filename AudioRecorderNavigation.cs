using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MZTools.Capture;

namespace MZTools;

internal sealed class AudioRecorderNavigation(Window owner) : IRecorderNavigation
{
    public async Task OpenAudioAsync(string path,RecordedAudioAction action,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!File.Exists(path)) throw new FileNotFoundException("The recorded segment no longer exists.",path);
        if(action==RecordedAudioAction.HeuristicImport)
        {
            var main=new MainWindow(); main.Show(); main.ImportRecordedAudio(path); return;
        }
        var visual=new AudioSignalAnalyzerWindow(owner,action==RecordedAudioAction.VisualAnalysis?path:null);
        visual.Show();
        if(action==RecordedAudioAction.ManualRecovery) await visual.OpenRecordedManualAsync(path);
    }
    public void RevealFile(string path)
    {
        var start=new ProcessStartInfo("explorer.exe") { UseShellExecute=false };
        start.ArgumentList.Add("/select,"+Path.GetFullPath(path)); Process.Start(start);
    }
}
