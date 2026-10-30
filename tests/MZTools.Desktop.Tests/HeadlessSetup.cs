using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(MZTools.Desktop.Tests.HeadlessSetup))]

namespace MZTools.Desktop.Tests;

public static class HeadlessSetup
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class RunnerTests
{
    [AvaloniaFact]
    public void RealSkiaFrameCanBeCaptured()
    {
        var window = new Window { Width = 400, Height = 240, Content = new TextBlock { Text = "MZTools" } };
        window.Show();
        try { using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); }
        finally { window.Close(); }
    }
}
