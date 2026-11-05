using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class SelectedAnalysisWindowTests
{
    [AvaloniaTheory]
    [InlineData("",false)]
    [InlineData("1",true)]
    [InlineData("invalid stale size",true)]
    [InlineData("32",false)]
    public async Task PayloadRecoveryFindsActualBytesWithBlankUnknownOrKnownLength(string length,bool unknown)
    {
        using var files=new AudioDocumentTests();var parent=await files.TapeWave();byte[] source=File.ReadAllBytes(parent.Path!);
        var original=WavHeuristicAnalyzer.AnalyzeFile(parent.Path!,includeCandidates:true);
        var payloads=original.Candidates.Where(c=>c.Kind==SharpBlockKind.Payload&&c.ChecksumValid).ToArray();Assert.NotEmpty(payloads);
        long first=payloads.Min(c=>c.SignalTrace!.LeaderStart),last=payloads.Max(c=>c.EndSample);
        var child=new RecoveryWindow(parent,first,last);child.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            child.GetVisualDescendants().OfType<ComboBox>().Single(c=>c.Name=="Recovery_scope").SelectedItem="Payload only";
            child.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="Recovery_length").Text=length;
            child.GetVisualDescendants().OfType<CheckBox>().Single(c=>c.Name=="Recovery_unknown").IsChecked=unknown;
            child.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="Recovery_thresholds").Text="1";
            Assert.True(await child.ScanAsync(),child.Model.Documents.Error);
            var result=Assert.IsType<AudioRegionRecoveryResult>(child.Session.Recovery);
            Assert.Equal(first,result.StartSample);Assert.Equal(last,result.EndSample);Assert.NotEmpty(result.Passes);
            Assert.Contains(result.Candidates,c=>c.Kind==SharpBlockKind.Payload&&c.ChecksumAvailable&&c.ChecksumValid&&c.Data.SequenceEqual(original.Records[0].Body.MzfBody));
            Assert.DoesNotContain(result.Candidates,c=>c.Kind==SharpBlockKind.Header);Assert.Empty(child.Session.Programs);
            Assert.Equal(2,child.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="SelectedAnalysisTabs").SelectedIndex);
            Assert.Single(parent.Programs);Assert.Null(parent.Recovery);Assert.Equal(source,File.ReadAllBytes(parent.Path!));
        }
        finally{child.Close();await Idle(child.Model.Documents);}
    }
    [AvaloniaFact]public async Task DraggedPreviewIntervalBecomesTheActualDecoderRange()
    {
        using var files=new AudioDocumentTests();var parent=await files.Open();var child=new RecoveryWindow(parent,100,1000);child.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();child.UpdateLayout();var graph=child.GetVisualDescendants().OfType<AudioChart>().Single(c=>c.Name=="SelectedAnalysisWaveform");
            Point At(double fraction)=>graph.TranslatePoint(new Point(64+(graph.Bounds.Width-76)*fraction,110),child)!.Value;
            child.MouseDown(At(.2),MouseButton.Left,RawInputModifiers.Shift);child.MouseMove(At(.6),RawInputModifiers.LeftMouseButton|RawInputModifiers.Shift);child.MouseUp(At(.6),MouseButton.Left,RawInputModifiers.Shift);Dispatcher.UIThread.RunJobs();
            long first=child.Session.SelectionStart,last=child.Session.SelectionEnd;Assert.InRange(first,101,998);Assert.InRange(last,first+1,999);
            child.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="Recovery_thresholds").Text="1";
            Assert.True(await child.ScanAsync(),child.Model.Documents.Error);Assert.Equal(first,child.Session.Recovery!.StartSample);Assert.Equal(last,child.Session.Recovery.EndSample);
            Assert.Equal(0,parent.ViewStart);Assert.Equal(AudioDocumentTests.Frames,parent.ViewEnd);Assert.Null(parent.Recovery);
        }
        finally{child.Close();await Idle(child.Model.Documents);}
    }
    private static async Task Idle(DocumentCoordinator documents)
    {
        for(int i=0;i<1000&&documents.IsBusy;i++){Dispatcher.UIThread.RunJobs();await Task.Delay(10,TestContext.Current.CancellationToken);}
        Assert.False(documents.IsBusy);Assert.Null(documents.Error);Dispatcher.UIThread.RunJobs();
    }
    private static void Click(Window window,Button button)
    {
        window.UpdateLayout();var point=button.TranslatePoint(new Point(button.Bounds.Width/2,button.Bounds.Height/2),window)!.Value;
        window.MouseDown(point,MouseButton.Left);window.MouseUp(point,MouseButton.Left);Dispatcher.UIThread.RunJobs();
    }
    [AvaloniaFact]public async Task AnalyzeSelectedOpensImmediatelyAndFreezesOnlyTheCapturedInterval()
    {
        using var files=new AudioDocumentTests();var parent=await files.TapeWave();byte[] original=File.ReadAllBytes(parent.Path!);Assert.Single(parent.Programs);
        var owner=new MainWindow(files.Shell);owner.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();parent.SetSelection([(100,1000)]);
            var analyze=owner.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Analyze selected audio…")&&b.IsEffectivelyVisible);
            Click(owner,analyze);var child=Assert.IsType<RecoveryWindow>(Assert.Single(owner.OwnedWindows));
            Assert.Equal(0,files.Dialogs.Forms);Assert.False(child.Model.Documents.IsBusy);Assert.Null(child.Session.Recovery);Assert.Empty(child.Session.Programs);
            Assert.NotSame(parent,child.Session);Assert.NotSame(files.Coordinator,child.Model.Documents);
            Assert.Equal(100,child.Session.SelectionStart);Assert.Equal(1000,child.Session.SelectionEnd);Assert.Equal(100,child.Session.ViewStart);Assert.Equal(1000,child.Session.ViewEnd);
            bool previewUsedDecoderBusy=false;child.Model.Documents.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(DocumentCoordinator.IsBusy)&&child.Model.Documents.IsBusy)previewUsedDecoderBusy=true;};
            await Task.Delay(400,TestContext.Current.CancellationToken);Dispatcher.UIThread.RunJobs();Assert.False(previewUsedDecoderBusy);Assert.True(child.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="RunDeeperAnalysis").IsEnabled);
            parent.SetSelection([(2000,3000)]);parent.ShowRange(2000,4000);
            child.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="Recovery_thresholds").Text="1";
            child.GetVisualDescendants().OfType<CheckBox>().Single(t=>t.Name=="Recovery_inverted").IsChecked=false;
            Click(child,child.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="RunDeeperAnalysis"));await Idle(child.Model.Documents);
            Assert.NotNull(child.Session.Recovery);Assert.Equal(100,child.Session.Recovery.StartSample);Assert.Equal(1000,child.Session.Recovery.EndSample);Assert.Empty(child.Session.Programs);
            Assert.Equal(2000,parent.SelectionStart);Assert.Equal(3000,parent.SelectionEnd);Assert.Equal(2000,parent.ViewStart);Assert.Equal(4000,parent.ViewEnd);Assert.Single(parent.Programs);
            Assert.Null(parent.Recovery);Assert.Equal(original,File.ReadAllBytes(parent.Path!));
            Capture(child,"selected-analysis-window.png");child.Close();await Idle(child.Model.Documents);
        }
        finally{foreach(var child in owner.OwnedWindows.ToArray())child.Close();owner.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task PreviewFitPanAndZoomNeverExpandPastTheSelectedScope()
    {
        using var files=new AudioDocumentTests();var parent=await files.Open();var child=new RecoveryWindow(parent,111,999);child.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();child.Session.Zoom(.00001);Assert.Equal(111,child.Session.ViewStart);Assert.Equal(999,child.Session.ViewEnd);
            child.Session.ShowRange(200,300);child.Session.Pan(long.MaxValue/4);Assert.Equal(999,child.Session.ViewEnd);child.Session.Fit();Assert.Equal(111,child.Session.ViewStart);Assert.Equal(999,child.Session.ViewEnd);
            var tabs=child.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="SelectedAnalysisTabs");
            Assert.Equal(new[]{"Visual analysis","Programs","Block candidates","Scan passes","Messages","Problems","Candidates / Recovery","Pass comparison"},tabs.Items.OfType<TabItem>().Select(t=>t.Header));
            var visual=child.GetVisualDescendants().OfType<TabControl>().Single(t=>t.Name=="SelectedAnalysisVisualTabs");Assert.Equal(new[]{"Waveform","Histogram","Scatter","Drift"},visual.Items.OfType<TabItem>().Select(t=>t.Header));
            Assert.Equal("Selected audio — deeper analysis / recovery preview",child.Title);Assert.Empty(child.Session.Programs);Capture(child,"selected-analysis-layout.png");
        }
        finally{child.Close();await Idle(child.Model.Documents);}
    }
    [AvaloniaFact]public async Task PreviewAlignmentAndExplicitRangeChangeAreIndependentOfTheParent()
    {
        using var files=new AudioDocumentTests();var parent=await files.Open();parent.SetSelection([(100,1000)]);var child=new RecoveryWindow(parent,100,1000);child.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();Click(child,child.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"1 sample →")));await Idle(child.Model.Documents);
            Assert.Equal(1,child.Session.AppliedTransform.RightShiftSamples);Assert.Equal("0",parent.RightShift);Assert.Equal(0,parent.AppliedTransform.RightShiftSamples);
            Assert.Equal(100,child.Session.ViewStart);Assert.Equal(1000,child.Session.ViewEnd);Assert.Equal(100,parent.SelectionStart);Assert.Equal(1000,parent.SelectionEnd);
            child.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="RecoveryFromSeconds").Text=(200d/48000).ToString("0.#########",System.Globalization.CultureInfo.CurrentCulture);
            child.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="RecoveryToSeconds").Text=(800d/48000).ToString("0.#########",System.Globalization.CultureInfo.CurrentCulture);
            child.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="Recovery_thresholds").Text="1";
            Assert.True(await child.ScanAsync(),child.Model.Documents.Error);Assert.Equal(200,child.Session.Recovery!.StartSample);Assert.Equal(800,child.Session.Recovery.EndSample);Assert.Null(parent.Recovery);
        }
        finally{child.Close();await Idle(child.Model.Documents);}
    }
    private static void Capture(Window window,string name)
    {
        string root=Environment.GetEnvironmentVariable("MZTOOLS_SELECTED_ANALYSIS_OUTPUT")??Path.Combine(Path.GetTempPath(),"mztools-selected-analysis");Directory.CreateDirectory(root);window.UpdateLayout();
        using var image=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96));image.Render(window);image.Save(Path.Combine(root,name),PngBitmapEncoderOptions.Default);
    }
}
