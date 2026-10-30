using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MZTools.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {MZTools.Platform.Audio.NativeAudioCodecs.Configure();AvaloniaXamlLoader.Load(this); }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode=ShutdownMode.OnLastWindowClose;
            var window=new Views.MainWindow(null,new Platform.DesktopPreferences());desktop.MainWindow=window;
            window.Opened+=async(_,_)=>{foreach(var path in desktop.Args??[]){if(path.StartsWith("-",StringComparison.Ordinal)||!System.IO.File.Exists(path))continue;await window.Model.Documents.OpenPathAsync(path);}};
        }
        base.OnFrameworkInitializationCompleted();
    }
}
