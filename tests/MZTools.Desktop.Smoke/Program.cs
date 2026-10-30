using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MZTools.Desktop;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Smoke;

internal static class Program
{
    internal static string Output = "";
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: MZTools.Desktop.Smoke <output-directory>"); return 2; }
        Output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Output);Environment.SetEnvironmentVariable("MZTOOLS_SETTINGS_ROOT",Path.Combine(Output,"preferences"));
        return AppBuilder.Configure<ReviewApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }
}

public sealed class ReviewApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is MainWindow window)
            window.Opened += async (_, _) => await Review(window, desktop);
    }

    private static async Task Review(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var checks = new List<object>(); var frames = new List<object>(); int exit = 0; bool clipboardRoundtripTested = false;
        void Check(bool ok, string name)
        {
            checks.Add(new { name, pass = ok });
            if (!ok) throw new InvalidOperationException(name);
        }
        try
        {
            await Task.Delay(400);
            Check(window.IsVisible && window.PlatformImpl is not null, "Native window is visible");
            Check(window.Model.Documents.Documents.Count == 0 && !window.Model.Documents.IsBusy, "Startup does not open files or devices");
            foreach (var (width, height) in new[] { (1440, 960), (1366, 768), (920, 640), (640, 480) })
            {
                window.Width = width; window.Height = height; await Task.Delay(120); window.UpdateLayout();
                var open = window.FindControl<Button>("OpenButton")!;
                Check(open.IsEffectivelyVisible && open.Bounds.Width > 0, $"Open reachable at {width}x{height}");
                var scale = window.RenderScaling;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
                    (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(window);
                var file = $"native-layout-{width}x{height}.png";
                bitmap.Save(Path.Combine(Program.Output, file), PngBitmapEncoderOptions.Default);
                frames.Add(new { file, logicalWidth = window.Bounds.Width, logicalHeight = window.Bounds.Height,
                    renderScaling = scale, pixels = bitmap.PixelSize.ToString(), origin = "Skia visual render of a real native window; not desktop screen capture" });
            }
            window.Width = 1366; window.Height = 768; await Task.Delay(100);
            var filter = window.FindControl<TextBox>("ModuleFilter")!;
            filter.Text = "Recorder"; Check(window.Model.VisibleModules.Count == 1, "Native input binding filters table");
            filter.Text = ""; window.Model.SelectedModule = window.Model.Modules[3];
            Check(window.Model.Heading == "Audio analysis workspace", "Navigation and selection share context");
            window.FindControl<Button>("OpenButton")!.Focus();
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F6 });
            Check(!window.FindControl<Button>("OpenButton")!.IsFocused, "F6 changes focus region");
            var clipboard = window.Clipboard ?? throw new NotSupportedException("No clipboard provider.");
            var previousText = await clipboard.TryGetTextAsync();
            using var previousData = await clipboard.TryGetDataAsync();
            bool richClipboard = previousData?.Formats.Any(f => f != DataFormat.Text) == true;
            if (richClipboard) Check(true,"Non-text/rich clipboard preserved; native text roundtrip UNTESTED for this run");
            else
            {
                try
                {
                    await clipboard.SetTextAsync(window.Model.Report);
                    Check(await clipboard.TryGetTextAsync() == window.Model.Report, "Native clipboard report roundtrip");
                    clipboardRoundtripTested = true;
                }
                finally { if (previousText is null) await clipboard.ClearAsync(); else await clipboard.SetTextAsync(previousText); }
            }

            bool pickerTested=Environment.GetEnvironmentVariable("MZTOOLS_NATIVE_NO_PICKER")!="1";
            if(pickerTested)
            {
            bool pickerSeen = false; int ticks = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) =>
            {
                ticks++;
                foreach (var dialog in desktop.Windows.Where(w => w != window && w.Owner == window).ToArray())
                { pickerSeen = true; dialog.Close(); }
                if (OperatingSystem.IsWindows() && NativePicker.CancelOwnedDialog(Environment.ProcessId)) pickerSeen = true;
                if (ticks > 75) { timer.Stop(); desktop.Shutdown(1); }
            };
            timer.Start();
            // Explicit smoke command requests the real platform picker; startup never does.
            bool opened;
            try { opened = await window.Model.Documents.OpenAsync(); }
            finally { timer.Stop(); }
            Check(pickerSeen, "Explicit Open displayed a real platform picker window");
            Check(!opened && window.Model.Documents.Documents.Count == 0 && !window.Model.Documents.HasError,
                "Cancel picker preserves empty document state without errors");
            pickerSeen = false; ticks = 0; timer.Start();
            string? savePath;
            try { savePath = await new DesktopDialogs(window).SaveAsAsync(new PickerDocument()); }
            finally { timer.Stop(); }
            Check(pickerSeen && savePath is null, "Explicit Save As picker can be cancelled without creating a file");
            Check(!window.Model.SaveCommand.CanExecute(null), "Save remains unavailable without document adapter");
            }
            var close = window.Model.Documents.CloseAllAsync(); Check(await close, "Native close coordinator succeeds");
            await MediaScenario.Run(Program.Output);
            await DiskScenario.Run(Program.Output);
            await AudioScenario.Run(Program.Output);
            await RecorderScenario.Run(Program.Output);
        }
        catch (Exception exception) { checks.Add(new { name = "Unhandled failure", pass = false, error = exception.ToString() }); exit = 1; }
        finally
        {
            File.WriteAllText(Path.Combine(Program.Output, "native-smoke.json"), JsonSerializer.Serialize(new
            {
                os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription,
                storageProvider = window.StorageProvider.GetType().FullName,
                backend = window.PlatformImpl?.GetType().FullName, checks, frames,
                hardwareTested = false, allNativeDpiScalesTested = false, nativePickerTested=Environment.GetEnvironmentVariable("MZTOOLS_NATIVE_NO_PICKER")!="1", clipboardRoundtripTested, exit
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Native smoke: {checks.Count} checks; exit={exit}; {Program.Output}");
            desktop.Shutdown(exit);
        }
    }
}

// Picker metadata only, never registered as a production format or document loader.
internal sealed class PickerDocument : DocumentSession
{
    public override Task WriteAsync(Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task ValidateSavedAsync(string temporaryPath, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal static class NativePicker
{
    private delegate bool EnumCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    internal static bool CancelOwnedDialog(int processId)
    {
        bool found = false;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner != processId) return true;
            var name = new System.Text.StringBuilder(80); GetClassName(handle, name, name.Capacity);
            if (name.ToString() == "#32770") { found = true; PostMessage(handle, 0x0010, 0, 0); }
            return true;
        }, 0);
        return found;
    }
}
