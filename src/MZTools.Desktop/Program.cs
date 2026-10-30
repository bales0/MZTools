using Avalonia;

namespace MZTools.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if(args.SequenceEqual(new[]{"--validate-runtime"}))
        {Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{runtime=System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,flac=MZTools.Platform.Audio.NativeAudioCodecs.Version,captureStarted=false}));return;}
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
