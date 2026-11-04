using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MZTools.Desktop.Views;

public sealed partial class AudioWorkspace
{
    private readonly Dictionary<int,Window> audioToolWindows=[];
    private readonly List<RecoveryWindow> selectedAnalysisWindows=[];
    private bool openingAudioTool;
    private void ConfigureWpfAudioWindows()
    {
        foreach(var tab in AudioTabs.Items.OfType<TabItem>().Skip(7))
        {tab.Width=tab.Height=tab.MinWidth=tab.MinHeight=0;tab.Padding=default;tab.IsHitTestVisible=tab.Focusable=false;}
        AudioTabs.SelectionChanged+=(_,e)=>
        {
            if(!openingAudioTool&&e.Source==AudioTabs&&AudioTabs.SelectedIndex>=7)
            {int index=AudioTabs.SelectedIndex;Dispatcher.UIThread.Post(()=>OpenWpfAudioTool(index));}
        };
        var menu=new ContextMenu();Waveform.ContextMenu=menu;
        void Item(string title,Action action){var item=new MenuItem{Header=title};item.Click+=(_,_)=>action();menu.Items.Add(item);}
        Item("Spectrum / spectrogram…",()=>OpenWpfAudioTool(7));
        Item("Recovery…",()=>AnalyzeSelectedAudio(null,new Avalonia.Interactivity.RoutedEventArgs()));Item("Analysis report…",()=>OpenWpfAudioTool(9));
        Item("Select frames…",()=>Model?.AudioSelectFramesCommand.Execute(null));Item("Measure selection",()=>Model?.AudioMeasureCommand.Execute(null));
        Item("Trigger…",()=>Model?.AudioTriggerCommand.Execute(null));Item("Audio properties…",()=>{if(TopLevel.GetTopLevel(this) is Window owner)ShowInspector(owner);});
        DetachedFromVisualTree+=(_,_)=>CloseWpfAudioTools();
    }
    private void OpenWpfAudioTool(int index)
    {
        if(openingAudioTool||index<7||index>=AudioTabs.Items.Count||TopLevel.GetTopLevel(this) is not Window owner)return;
        openingAudioTool=true;
        try
        {
            if(observed is not null)observed.ViewIndex=0;AudioTabs.SelectedIndex=0;
            if(audioToolWindows.TryGetValue(index,out var existing)){existing.Activate();return;}
            if(AudioTabs.Items[index] is not TabItem tab||tab.Content is not Control content)return;
            tab.Content=null;
            var window=new Window{Title=index switch{7=>"Audio spectrum / spectrogram",8=>"Analyze selected audio — recovery",_=>"Audio analysis report"},Width=1100,Height=800,MinWidth=700,MinHeight=480,DataContext=Model,Content=new Border{Padding=new Thickness(10),Child=content},WindowStartupLocation=WindowStartupLocation.CenterOwner};
            audioToolWindows.Add(index,window);window.Closed+=(_,_)=>{if(window.Content is Border border)border.Child=null;window.Content=null;tab.Content=content;audioToolWindows.Remove(index);};window.Show(owner);
        }
        finally{openingAudioTool=false;}
    }
    private void AnalyzeSelectedAudio(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        if(Model?.ActiveAudio is not{} source||TopLevel.GetTopLevel(this) is not Window owner)return;
        try
        {
            source.RequireSingleInterval();
            long first=source.HasVisualSelection?source.SelectionStart:source.ViewStart,last=source.HasVisualSelection?source.SelectionEnd:source.ViewEnd;
            var window=new RecoveryWindow(source,first,last);selectedAnalysisWindows.Add(window);
            window.Closed+=(_,_)=>selectedAnalysisWindows.Remove(window);window.Show(owner);
        }
        catch(Exception error){Model.Documents.ReportError(error.Message);}
    }
    private void CloseWpfAudioTools(){foreach(var window in audioToolWindows.Values.ToArray())window.Close();foreach(var window in selectedAnalysisWindows.ToArray())window.Close();}
}
