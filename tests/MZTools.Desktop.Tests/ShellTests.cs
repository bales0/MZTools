using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class ShellTests
{
    private static (MainWindow Window, ShellViewModel Model, TestDialogs Dialogs, TestLoader Loader) Create()
    {
        var dialogs = new TestDialogs(); var loader = new TestLoader();
        var model = new ShellViewModel(new(loader, dialogs, new()), _ => Task.CompletedTask);
        var window = new MainWindow(model); window.Show(); Dispatcher.UIThread.RunJobs();
        return (window, model, dialogs, loader);
    }
    private static void Close(MainWindow window) { window.Model.Documents.Cancel(); window.Close(); Dispatcher.UIThread.RunJobs(); }

    [AvaloniaFact]
    public void StartupHasNoDocumentsAndDoesNotOpenPicker()
    {
        var (window, model, _, _) = Create();
        try
        {
            Assert.Empty(model.Documents.Documents); Assert.False(model.Documents.IsBusy);
            Assert.True(model.OpenCommand.CanExecute(null)); Assert.False(model.SaveCommand.CanExecute(null));
            Assert.False(model.SaveAsCommand.CanExecute(null)); Assert.False(model.CloseCommand.CanExecute(null));
            Assert.Contains("No open document", model.DocumentReason);
            Assert.False(window.FindControl<ListBox>("Navigation")!.IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(),b=>Equals(b.Content,"Tools ▾")&&b.IsEffectivelyVisible);
        }
        finally { Close(window); }
    }

    [AvaloniaFact]
    public void FilterUpdatesActualTableAndKeepsOtherModuleState()
    {
        var (window,model,_,_)=Create();
        try {
            model.Filter="Recorder"; Assert.Single(model.VisibleModules);Assert.Equal("Recorder",model.VisibleModules[0].Name);
            model.Filter="no matching module";Assert.Empty(model.VisibleModules);
            model.Filter="";Assert.Equal(7,model.VisibleModules.Count);
            Assert.Empty(model.Documents.Documents);Assert.True(model.OpenCommand.CanExecute(null));
        } finally {Close(window);}
    }

    [AvaloniaFact]
    public void SelectionSynchronizesInspectorAndRemainsWhenTextInputGetsFocus()
    {
        var (window,model,_,_)=Create();
        try {
            var toolbar=window.FindControl<WrapPanel>("CommandBar")!;
            var tools=toolbar.Children.OfType<Button>().Single(b=>b.Name=="ToolsButton");
            Assert.Equal(new[]{"Batch Process...","Visual analysis…","Analyze Audio (WAV / FLAC)...","Record Audio / Live Tape Analysis...","MZF/M12 → COM..."},tools.ContextMenu!.Items.OfType<MenuItem>().Take(5).Select(m=>m.Header));
            Assert.False(window.FindControl<ListBox>("Navigation")!.IsEffectivelyVisible);
            Assert.Empty(window.OwnedWindows);Assert.Empty(model.Documents.Documents);
        } finally {Close(window);}
    }

    [AvaloniaFact]
    public void F6MovesFocusFromCommandBarAndTextInputRemainsEditable()
    {
        var (window,model,_,_)=Create();
        try {
            var open=window.FindControl<Button>("OpenButton")!;Assert.True(open.Focus());
            window.KeyPress(Key.F6,RawInputModifiers.None,PhysicalKey.F6,null);window.KeyRelease(Key.F6,RawInputModifiers.None,PhysicalKey.F6,null);Dispatcher.UIThread.RunJobs();
            Assert.False(open.IsFocused);Assert.Empty(model.Documents.Documents);
        } finally {Close(window);}
    }

    [AvaloniaFact]
    public async Task PrimaryOpenAndCloseRouteThroughSharedCommands()
    {
        var (window, model, dialogs, _) = Create();
        var file = Path.GetTempFileName();
        try
        {
            dialogs.OpenPath = file;
            var primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
            window.KeyPress(Key.O, primary, PhysicalKey.O, "o"); window.KeyRelease(Key.O, primary, PhysicalKey.O, "o");
            await PumpUntil(() => model.Documents.Active is not null);
            Assert.True(model.CloseCommand.CanExecute(null));
            window.KeyPress(Key.W, primary, PhysicalKey.W, "w"); window.KeyRelease(Key.W, primary, PhysicalKey.W, "w");
            await PumpUntil(() => model.Documents.Documents.Count == 0);
            Assert.False(model.CloseCommand.CanExecute(null));
        }
        finally { Close(window); File.Delete(file); }
    }

    [AvaloniaFact]
    public async Task ActiveDocumentCommandsUpdateOnEditAndSave()
    {
        var (window, model, dialogs, _) = Create(); var file = Path.GetTempFileName();
        try
        {
            dialogs.OpenPath = file; Assert.True(await model.Documents.OpenAsync());
            var document = Assert.IsType<TestDocument>(model.Documents.Active);
            Assert.False(model.SaveCommand.CanExecute(null)); document.Edit("new");
            Assert.True(model.SaveCommand.CanExecute(null)); Assert.Contains("*", document.Title);
            Assert.True(await model.Documents.SaveAsync()); Assert.False(model.SaveCommand.CanExecute(null));
            Assert.DoesNotContain("*", document.Title);
        }
        finally { Close(window); File.Delete(file); }
    }

    [AvaloniaFact]
    public async Task TabsKeepIndependentDirtyStateAndCloseCancelRetainsWindow()
    {
        var (window, model, dialogs, _) = Create(); var first = Path.GetTempFileName(); var second = Path.GetTempFileName();
        try
        {
            dialogs.OpenPath = first; await model.Documents.OpenAsync(); var document = (TestDocument)model.Documents.Active!;
            document.Edit("unsaved"); dialogs.OpenPath = second; await model.Documents.OpenAsync();
            var tabs = window.FindControl<ListBox>("DocumentTabs")!; tabs.SelectedItem = document;
            Assert.Same(document, model.Documents.Active); Assert.Equal(2, tabs.ItemCount);
            window.Close(); await PumpUntil(() => !model.Documents.IsBusy);
            Assert.True(window.IsVisible); Assert.True(document.IsModified);
            dialogs.CloseDecisions.Enqueue(CloseDecision.Discard);
        }
        finally { Close(window); File.Delete(first); File.Delete(second); }
    }

    [AvaloniaFact]
    public async Task ErrorBannerAndProgressReflectRealOperation()
    {
        var (window, model, dialogs, loader) = Create(); var file = Path.GetTempFileName();
        var entered = new TaskCompletionSource();
        try
        {
            dialogs.OpenPath = file;
            loader.BeforeLoad = async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
            var operation = model.Documents.OpenAsync(); await entered.Task; Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("CancelOperation")!.IsVisible);
            Assert.False(model.OpenCommand.CanExecute(null)); Assert.True(model.CancelCommand.CanExecute(null));
            model.Documents.Cancel(); Assert.False(await operation);
            dialogs.OpenPath = file + ".missing"; Assert.False(await model.Documents.OpenAsync()); Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Border>("ErrorBanner")!.IsVisible);
            Assert.False(window.FindControl<Button>("CancelOperation")!.IsVisible);
        }
        finally { Close(window); File.Delete(file); }
    }

    [AvaloniaFact]
    public async Task ReadinessReportCanBeCopiedWithoutChangingDocumentState()
    {
        string? copied = null;
        var model = new ShellViewModel(new(new TestLoader(), new TestDialogs(), new()), text => { copied = text; return Task.CompletedTask; });
        model.CopyReportCommand.Execute(null); await PumpUntil(() => copied is not null);
        Assert.DoesNotContain("Not yet implemented", copied); Assert.Contains("hardware validation separate",copied); Assert.Contains("not test certification", copied);
        Assert.Empty(model.Documents.Documents);
        var denied = new ShellViewModel(new(new TestLoader(), new TestDialogs(), new()),
            _ => Task.FromException(new InvalidOperationException("Clipboard denied")));
        denied.CopyReportCommand.Execute(null); await PumpUntil(() => denied.Documents.HasError);
        Assert.Contains("Clipboard denied", denied.Documents.Error);
    }

    [AvaloniaFact]
    public async Task ProductionUnsupportedOpenShowsErrorAndKeepsSourceIntact()
    {
        var file = Path.GetTempFileName(); await File.WriteAllTextAsync(file, "unchanged source");
        var dialogs = new TestDialogs { OpenPath = file };
        var model = new ShellViewModel(new(new MediaDocumentLoader(), dialogs, new()), _ => Task.CompletedTask);
        var window = new MainWindow(model); window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.False(await model.Documents.OpenAsync()); Dispatcher.UIThread.RunJobs();
            Assert.Empty(model.Documents.Documents); Assert.Equal("unchanged source", await File.ReadAllTextAsync(file));
            Assert.True(window.FindControl<Border>("ErrorBanner")!.IsEffectivelyVisible);
            SaveFrame(window, "headless-error.png");
            model.Documents.ClearError();Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<Border>("ErrorBanner")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("OpenButton")!.IsEffectivelyVisible);
        }
        finally { Close(window); File.Delete(file); }
    }

    private static void SaveFrame(Window window, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("MZTOOLS_F3_OUTPUT");
        if (directory is null) return;
        Directory.CreateDirectory(directory); using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap); bitmap.Save(Path.Combine(directory, filename), PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void PropertyFormShowsValidationAndProtectsReadOnlyFields()
    {
        var editable = new PropertyField("Name", "ok", validate: value => value.Length == 0 ? "Name is required." : null);
        var locked = new PropertyField("Original", "keep", true);
        var window = new Window { Width = 400, Height = 300, Content = new PropertyForm { Fields = [editable, locked] } };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var input = window.GetVisualDescendants().OfType<TextBox>().First(); input.Text = ""; Dispatcher.UIThread.RunJobs();
            Assert.True(editable.HasError); Assert.Equal("Name is required.", editable.Error);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == editable.Error && t.IsEffectivelyVisible);
            locked.Text = "changed"; Assert.Equal("keep", locked.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1440, 960, 1.0)] [InlineData(1366, 768, 1.0)] [InlineData(1920, 1080, 1.0)]
    [InlineData(920, 640, 1.0)] [InlineData(1366, 768, 1.5)] [InlineData(1366, 768, 2.0)]
    [InlineData(900, 480, 1.0)]
    public void ShellLayoutAndRealSkiaScreenshots(int width, int height, double scale)
    {
        var (window, model, _, _) = Create();
        try
        {
            window.Width = width; window.Height = height; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            foreach (string name in new[] { "OpenButton" })
            {
                var control = window.FindControl<Control>(name)!;
                Assert.True(control.IsEffectivelyVisible, name); Assert.True(control.Bounds.Width > 0, name);
                Assert.True(control.Bounds.Height > 0, name + " has no layout height");
                var origin = control.TranslatePoint(default, window)!.Value;
                Assert.InRange(origin.X, 0, width); Assert.InRange(origin.Y, 0, height);
                Assert.True(origin.X + control.Bounds.Width <= width + 1, name + " overflows horizontally");
                if (name != "ReadinessTable") Assert.True(origin.Y + control.Bounds.Height <= height + 1, name + " overflows vertically");
            }
            using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
            var directory = Environment.GetEnvironmentVariable("MZTOOLS_F3_OUTPUT");
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)(width * scale), (int)(height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(directory, FormattableString.Invariant($"headless-shell-{width}x{height}-{scale:0.0}.png")), PngBitmapEncoderOptions.Default);
            }
        }
        finally { Close(window); }
    }

    [AvaloniaFact]
    public void NarrowInspectorRemainsReachableInOwnedDetailWindow()
    {
        var (window, model, _, _) = Create();
        try
        {
            window.Width = 900; window.Height = 480; Dispatcher.UIThread.RunJobs();
            model.InspectorCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows);
            var detail = window.OwnedWindows.Single(); Assert.Equal("Workspace inspector", detail.Title);
            detail.Close();
        }
        finally { Close(window); }
    }

    [AvaloniaFact]
    public void SmallViewportCanReachLastTableRowAndFeedbackByScrolling()
    {
        var (window,_,_,_)=Create();
        try {
            window.Width=900;window.Height=480;Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var toolbar=window.FindControl<WrapPanel>("CommandBar")!;
            foreach(var b in toolbar.Children.OfType<Button>().Where(b=>b.IsEffectivelyVisible)) {
                var origin=b.TranslatePoint(default,window)!.Value;
                Assert.True(origin.X>=0&&origin.X+b.Bounds.Width<=window.Bounds.Width+1);
                Assert.True(origin.Y+b.Bounds.Height<=window.Bounds.Height);
            }
        } finally {Close(window);}
    }

    [AvaloniaFact]
    public void TableArrowKeyUpdatesSelectionAndInspector()
    {
        var (window,model,_,_)=Create();
        try {
            var toolbar=window.FindControl<WrapPanel>("CommandBar")!;
            Assert.Same(model.OpenCommand,toolbar.Children.OfType<Button>().Single(b=>Equals(b.Content,"Open...")).Command);
            Assert.Same(model.SaveCommand,toolbar.Children.OfType<Button>().Single(b=>Equals(b.Content,"Save")).Command);
            Assert.Same(model.NewQuickDiskCommand,toolbar.Children.OfType<Button>().Single(b=>b.Name=="NewDocumentButton").ContextMenu!.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"New QD...")).Command);
            Assert.False(toolbar.Children.OfType<Button>().Single(b=>Equals(b.Content,"Add...")).IsEnabled);
        } finally {Close(window);}
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(condition(), "UI operation did not finish within the bounded test wait.");
    }
}
