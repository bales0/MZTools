using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MZTools.Capture;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

public sealed partial class RecorderWindow : Window
{
    public RecorderModel Model {get;}
    private readonly IRecorderNavigation navigation;
    private readonly Func<string,bool> destinationAllowed;
    private readonly DispatcherTimer refresh=new(){Interval=TimeSpan.FromMilliseconds(75)};
    private bool approved,closing;
    public RecorderWindow():this(new(new RecorderBackendFactory()),new DesktopRecorderNavigation(null)){}
    public RecorderWindow(RecorderModel model,IRecorderNavigation navigation,Func<string,bool>? destinationAllowed=null)
    {
        InitializeComponent();Model=model;DataContext=model;this.navigation=navigation;this.destinationAllowed=destinationAllowed??(_=>true);
        LiveWave.LiveZoom+=(factor,anchor)=>model.ZoomWave(factor,anchor);LiveWave.LivePan+=model.PanWave;LiveWave.LiveSelected+=model.SelectFrames;
        LivePulses.LiveZoom+=(factor,anchor)=>model.ZoomPulse(factor,anchor);LivePulses.LivePan+=frames=>model.PanPulse(frames);
        refresh.Tick+=async(_,_)=>await model.RefreshAsync();Opened+=(_,_)=>refresh.Start();Closing+=CloseRecorder;Closed+=(_,_)=>refresh.Stop();
    }
    private async void Enumerate(object? sender,RoutedEventArgs e)=>await Model.EnumerateAsync();
    private async void Monitor(object? sender,RoutedEventArgs e)=>await Model.StartAsync();
    private async void Stop(object? sender,RoutedEventArgs e)=>await Model.StopAsync();
    private void CancelOperation(object? sender,RoutedEventArgs e)=>Model.Cancel();
    private async void Record(object? sender,RoutedEventArgs e)
    {
        try
        {
            var dialogs=new DesktopDialogs(this);var path=await dialogs.PickDiskOutputAsync("Record WAV","recording.wav");if(path is null)return;
            if(!Path.GetExtension(path).Equals(".wav",StringComparison.OrdinalIgnoreCase))throw new IOException("Choose a .wav recording destination.");
            if(!destinationAllowed(path)||!destinationAllowed(path+".json"))throw new IOException("The destination belongs to an open document or source set.");
            bool exists=File.Exists(path)||File.Exists(path+".json");if(exists&&!await dialogs.ConfirmOverwriteAsync(path))return;await Model.StartAsync(path,exists);
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){Model.ReportError(ex.Message);}
    }
    private async void Discard(object? sender,RoutedEventArgs e)
    {if(await new DesktopDialogs(this).ConfirmDiscardRecordingAsync())await Model.DiscardAsync();}
    private void ResetClipping(object? sender,RoutedEventArgs e)=>Model.ResetClipping();
    private void ApplyPulses(object? sender,RoutedEventArgs e)=>Model.ApplyPulses();
    private void FitWave(object? sender,RoutedEventArgs e)=>Model.FitWave();
    private void FitPulse(object? sender,RoutedEventArgs e)=>Model.FitPulse();
    private void FitPulseReferences(object? sender,RoutedEventArgs e)=>Model.FitPulseReference();
    private void ZeroEdges(object? sender,RoutedEventArgs e)=>Model.ZeroEdges();
    private void ClearEdges(object? sender,RoutedEventArgs e)=>Model.ClearEdges();
    private void ClearComparison(object? sender,RoutedEventArgs e)=>Model.ClearComparison();
    private async void UserReference(object? sender,RoutedEventArgs e)
    {
        try{var answer=await new DesktopDialogs(this).DiskFormAsync("User pulse reference",[new("name","Reference name","User reference"),new("sh","SHORT High µs","200"),new("sl","SHORT Low µs","200"),new("lh","LONG High µs","400"),new("ll","LONG Low µs","400")],"Session-local user values, not independent measured evidence. Apply pulse settings explicitly to use this reference.");if(answer is null)return;double Read(string key)=>double.Parse(answer[key],System.Globalization.CultureInfo.InvariantCulture);Model.AddPulseReference(answer["name"],Read("sh"),Read("sl"),Read("lh"),Read("ll"));}catch(Exception ex)when(ex is not OutOfMemoryException){Model.ReportError(ex.Message);}
    }
    private void SaveA(object? sender,RoutedEventArgs e)=>Model.SaveAlignment(true);
    private void SaveB(object? sender,RoutedEventArgs e)=>Model.SaveAlignment(false);
    private async Task Open(RecordedAudioAction action)
    {
        try
        {
            var report=Model.SelectedRecording??throw new InvalidOperationException("Select a finalized recording first.");var recording=report.Recording??throw new InvalidOperationException("This run did not record a file.");
            if(report.Integrity!=CaptureIntegrity.Complete||!recording.AnalyzerCompatible||!recording.SidecarFinalized)throw new InvalidOperationException("Analysis requires a complete compatible finalized recording. Preserve the native/partial recording and inspect its report separately.");
            await navigation.OpenAudioAsync(recording.SidecarPath??recording.Segments[0].Path,action);
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){Model.ReportError(ex.Message);}
    }
    private async void OpenVisual(object? sender,RoutedEventArgs e)=>await Open(RecordedAudioAction.VisualAnalysis);
    private async void OpenHeuristic(object? sender,RoutedEventArgs e)=>await Open(RecordedAudioAction.HeuristicImport);
    private async void OpenRecovery(object? sender,RoutedEventArgs e)=>await Open(RecordedAudioAction.ManualRecovery);
    private void Reveal(object? sender,RoutedEventArgs e)
    {try{if(Model.SelectedRecording?.Recording?.Segments.FirstOrDefault() is { } segment)navigation.RevealFile(segment.Path);else throw new InvalidOperationException("Select a saved recording first.");}catch(Exception ex)when(ex is not OutOfMemoryException){Model.ReportError(ex.Message);}}
    private async void CloseRecorder(object? sender,WindowClosingEventArgs e)
    {
        if(approved)return;e.Cancel=true;if(closing)return;closing=true;
        try
        {
            if(Model.IsActive&&!await new DesktopDialogs(this).ConfirmRecorderCloseAsync())return;
            refresh.Stop();Model.Cancel();await Model.DisposeAsync();approved=true;Dispatcher.UIThread.Post(Close);
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){Model.ReportError(ex.Message);refresh.Start();}
        finally{closing=false;}
    }
}
