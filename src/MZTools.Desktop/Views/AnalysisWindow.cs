using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

/// <summary>Independent tool lifetime, as in the WPF analysis and Batch windows.</summary>
public sealed class AnalysisWindow:Window
{
    public ShellViewModel Model {get;}
    private bool approved,closing;
    public AnalysisWindow(bool batch=false)
    {
        Title=batch?"Batch Process":"Visual Media Analysis — WAV / FLAC";
        Width=batch?1250:1200;Height=850;MinWidth=850;MinHeight=650;
        var dialogs=new DesktopDialogs(this){AudioOnly=!batch};
        Model=new ShellViewModel(new(new MediaDocumentLoader(dialogs,!batch),dialogs,new()),async text=>
        {
            var clipboard=Clipboard??throw new NotSupportedException("Clipboard is unavailable.");
            await clipboard.SetTextAsync(text);
        },dialogs);
        Model.InspectorVisible=false;DataContext=Model;
        var root=new Grid{RowDefinitions=new RowDefinitions("Auto,*,Auto"),Margin=new Thickness(10),RowSpacing=6};
        var actions=new WrapPanel{Orientation=Orientation.Horizontal};
        if(!batch)
        {
            actions.Children.Add(new Button{Content="Open audio / recording…",Command=Model.OpenCommand,Margin=new Thickness(4),Padding=new Thickness(10,4)});
            actions.Children.Add(new Button{Content="Cancel analysis",Command=Model.CancelCommand,Margin=new Thickness(4)});
            actions.Children.Add(new Button{Content="Export report…",Command=Model.AudioReportExportCommand,Margin=new Thickness(4)});
            actions.Children.Add(new Button{Content="Export selection to WAV…",Command=Model.AudioWaveExportCommand,Margin=new Thickness(4)});
            actions.Children.Add(new Button{Content="Copy summary",Command=Model.CopyReportCommand,Margin=new Thickness(4)});
        }
        var header=new StackPanel{Spacing=4};header.Children.Add(actions);
        if(!batch)
        {
            var heading=new TextBlock{FontSize=14,Margin=new Thickness(5),TextWrapping=Avalonia.Media.TextWrapping.Wrap};
            heading.Bind(TextBlock.TextProperty,new Binding("ActiveAudio.WpfHeading"){TargetNullValue="Captured / measured PCM — select a WAV or FLAC capture. Analysis does not change the file."});header.Children.Add(heading);
            var progress=new ProgressBar{Height=6,Minimum=0,Maximum=1,Foreground=Avalonia.Media.Brushes.Green};progress.Bind(ProgressBar.ValueProperty,new Binding("WpfAudioProgress"));header.Children.Add(progress);
            var state=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap,Margin=new Thickness(5)};state.Bind(TextBlock.TextProperty,new Binding("WpfAudioStatus"));header.Children.Add(state);
        }
        root.Children.Add(header);
        Control editor=batch?new ScrollViewer{Content=new BatchWorkspace{DataContext=Model},HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled}:new AudioWorkspace{DataContext=Model};
        Grid.SetRow(editor,1);root.Children.Add(editor);
        var status=new StackPanel{Spacing=4};
        var error=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap,Foreground=Avalonia.Media.Brushes.Firebrick};error.Bind(TextBlock.TextProperty,new Binding("Documents.Error"));
        var message=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap,IsVisible=batch};message.Bind(TextBlock.TextProperty,new Binding("Documents.Status"));
        status.Children.Add(error);status.Children.Add(message);Grid.SetRow(status,2);root.Children.Add(status);Content=root;
        Closing+=async(_,e)=>
        {
            if(approved)return;e.Cancel=true;if(closing)return;closing=true;
            try
            {
                Model.Documents.Cancel();
                while(Model.Documents.IsBusy)await Task.Delay(20);
                if(await Model.Documents.CloseAllAsync()){approved=true;Close();}
            }
            finally{closing=false;}
        };
    }
    public Task<bool> OpenPathAsync(string path)=>Model.Documents.OpenPathAsync(path);
    public async Task PrepareBatchAsync()
    {
        if(!Model.BatchPreviewCommand.CanExecute(null))return;
        Model.BatchPreviewCommand.Execute(null);
        while(Model.Documents.IsBusy)await Task.Delay(20);
    }
}
