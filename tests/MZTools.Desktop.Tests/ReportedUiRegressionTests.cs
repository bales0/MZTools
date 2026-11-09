using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class ReportedUiRegressionTests
{
    [AvaloniaTheory][InlineData(1)][InlineData(2)]
    public async Task MainAudioAutomaticallyShowsMonoOrStereoOverlayWithoutAChannelChoice(int channels)
    {
        using var files=new AudioDocumentTests();var session=await files.Open(files.Wave(channels:channels));var window=new MainWindow(files.Shell);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var selector=window.GetVisualDescendants().OfType<ComboBox>().Single(c=>AutomationProperties.GetName(c)=="Displayed audio channels");
            Assert.False(selector.IsEffectivelyVisible);Assert.Equal(channels==1?0:2,session.ChannelMode);Assert.Contains(channels==1?"Mono":"Overlay L/R",session.AnalysisInputStatus);
            Assert.Equal(new AudioPcmTransform(),session.EffectiveTransform);
        }
        finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task CurrentPulseMeasurementIsVisibleAfterPreviousRecoveryBecomesOutdated()
    {
        using var files=new AudioDocumentTests();var parent=await files.Open();var child=new RecoveryWindow(parent,100,1000);child.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();child.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="Recovery_thresholds").Text="1";
            Assert.True(await child.ScanAsync(),child.Model.Documents.Error);child.Session.ChannelMode=1;
            Assert.True(await child.Session.ApplyTransformKeepingViewportAsync(TestContext.Current.CancellationToken));
            Assert.False(child.Session.IsRecoveryCurrent);Assert.True(await child.Session.MeasureAsync(TestContext.Current.CancellationToken));
            Assert.NotNull(child.Session.MeasuredBlock);Assert.Equal(2,child.Session.MeasuredBlock.Channel);
            Assert.Contains(child.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.StartsWith("Current pulse measurement",StringComparison.Ordinal)==true);
        }
        finally{child.Close();await Idle(child.Model.Documents);}
    }
    [AvaloniaFact]public async Task IndependentWaveformZoomLoadsExactPcmInsteadOfExpandingTheOverviewIntoASolidFill()
    {
        using var files=new AudioDocumentTests();var source=await files.Open();byte[] original=File.ReadAllBytes(source.Path!);
        var model=new AudioSignalAnalysis{Source=source.Model.Source,SourceLength=source.Model.SourceLength,SourceModifiedUtc=source.Model.SourceModifiedUtc,SampleRate=48000,BitsPerSample=16,ChannelCount=2,FrameCount=AudioDocumentTests.Frames,Waveform=new([new WaveformBucket(0,AudioDocumentTests.Frames,-.5f,.5f,-.5f,.5f)],AudioDocumentTests.Frames,2)};
        var session=new AudioSession(model,source.SourceHashes.ToDictionary(p=>p.Key,p=>p.Value));
        var chart=new AudioChart{Session=session,Kind=AudioPlotKind.Waveform};var window=new Window{Width=1200,Height=300,Content=chart};window.Show();
        try
        {
            session.ShowRange(100,140);Dispatcher.UIThread.RunJobs();
            for(int i=0;i<300;i++)
            {
                Dispatcher.UIThread.RunJobs();var buckets=session.DisplayWaveform.VisibleBuckets(100,140,1200);
                if(buckets.Count==40&&buckets.All(b=>b.EndSample-b.StartSample==1))break;
                await Task.Delay(10,TestContext.Current.CancellationToken);
            }
            var actual=session.DisplayWaveform.VisibleBuckets(100,140,1200);Assert.Equal(40,actual.Count);
            foreach(var bucket in actual){Assert.Equal(1,bucket.EndSample-bucket.StartSample);int frame=(int)bucket.StartSample;Assert.Equal(BitConverter.ToInt16(original,44+frame*4)/32768d,bucket.LeftMin);Assert.Equal(BitConverter.ToInt16(original,44+frame*4+2)/32768d,bucket.RightMin);}
            Assert.Equal(original,File.ReadAllBytes(source.Path!));
        }
        finally{window.Close();session.Dispose();Dispatcher.UIThread.RunJobs();}
    }
    private static async Task Idle(DocumentCoordinator documents)
    {
        for(int i=0;i<1000&&documents.IsBusy;i++){Dispatcher.UIThread.RunJobs();await Task.Delay(10,TestContext.Current.CancellationToken);}
        Assert.False(documents.IsBusy);Assert.Null(documents.Error);Dispatcher.UIThread.RunJobs();
    }
    [AvaloniaFact]public void FloatingSelectionDetailsCanBeClosedReopenedAndToggledWithoutReusingVisuals()
    {
        using var files=new AudioDocumentTests();var session=new TapeSession(new TapeDocument{Format=TapeDocumentFormat.QdFlashFloppy,QuickDiskProfile=QuickDiskPhysicalProfile.FlashFloppy,IsModified=true});files.Coordinator.Add(session);
        var window=new QuickDiskVisualWindow(files.Shell);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();
            var surface=window.GetVisualDescendants().OfType<QuickDiskSurfaceCanvas>().Single();var toggle=window.GetVisualDescendants().OfType<CheckBox>().Single(c=>Equals(c.Content,"Floating selection details"));
            Assert.False(toggle.IsChecked);var point=surface.TranslatePoint(surface.PointAt(session.Snapshot!.Layout.Length*.25),window)!.Value;
            window.MouseDown(point,MouseButton.Left);window.MouseUp(point,MouseButton.Left);Dispatcher.UIThread.RunJobs();Assert.NotNull(session.SelectedRegion);Assert.Empty(window.OwnedWindows);
            Control? oldContent=null;
            for(int i=0;i<3;i++)
            {
                toggle.IsChecked=true;Dispatcher.UIThread.RunJobs();var detail=Assert.Single(window.OwnedWindows);
                var text=detail.GetVisualDescendants().OfType<TextBlock>().Single();Assert.NotSame(oldContent,text);oldContent=text;Assert.Contains("Selected regions:",text.Text);
                toggle.IsChecked=false;Assert.False(detail.IsVisible);toggle.IsChecked=true;Assert.True(detail.IsVisible);
                detail.Close();Dispatcher.UIThread.RunJobs();Assert.False(toggle.IsChecked);Assert.Empty(window.OwnedWindows);
            }
            toggle.IsChecked=true;window.Close();Dispatcher.UIThread.RunJobs();Assert.False(window.IsVisible);
        }
        finally{if(window.IsVisible)window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task SingleClickAnywhereInWaveformSelectsTheDetectedBlockAndTableWithoutChangingZoom()
    {
        using var files=new AudioDocumentTests();var session=await files.TapeWave();byte[] original=File.ReadAllBytes(session.Path!);
        var window=new MainWindow(files.Shell);window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();window.UpdateLayout();var waveform=window.GetVisualDescendants().OfType<AudioChart>().Single(c=>c.Name=="Waveform");
            var expected=session.Blocks.First(b=>b.Block=="Payload");long first=session.ViewStart,last=session.ViewEnd;long frame=expected.Start+(expected.End-expected.Start)/2;
            double x=64+(frame-first)/(double)(last-first)*(waveform.Bounds.Width-76);var point=waveform.TranslatePoint(new Point(x,110),window)!.Value;
            window.MouseDown(point,MouseButton.Left,RawInputModifiers.None);window.MouseUp(point,MouseButton.Left,RawInputModifiers.None);Dispatcher.UIThread.RunJobs();
            Assert.Same(expected,session.SelectedBlock);Assert.Equal(expected.Start,session.SelectionStart);Assert.Equal(expected.End,session.SelectionEnd);
            Assert.Same(expected,window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="AudioBlocks").SelectedItem);
            Assert.Equal(first,session.ViewStart);Assert.Equal(last,session.ViewEnd);Assert.Equal(original,File.ReadAllBytes(session.Path!));
        }
        finally{window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task DeepAnalysisOffsetButtonsShiftActualRightPcmAndPreserveViewportAndSelection()
    {
        using var files=new AudioDocumentTests();var session=await files.Open();byte[] original=File.ReadAllBytes(session.Path!);session.ShowRange(100,400);session.SetSelection([(120,350)]);
        var parent=session;session=parent.CreateSelectedPreview(120,350);files.Coordinator.Add(session);session.ShowRange(120,350);
        var window=new MainWindow(files.Shell);window.Show();
        try
        {
            session.ViewIndex=8;Dispatcher.UIThread.RunJobs();var deep=window.OwnedWindows.Single(w=>w.Title=="Analyze selected audio — recovery");
            Assert.True(deep.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Deep analysis right channel offset").IsEffectivelyVisible);
            var increment=deep.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"+1"));deep.UpdateLayout();var point=increment.TranslatePoint(new Point(increment.Bounds.Width/2,increment.Bounds.Height/2),deep)!.Value;deep.MouseDown(point,MouseButton.Left);deep.MouseUp(point,MouseButton.Left);await Idle(files.Coordinator);
            Assert.Equal("0",parent.RightShift);Assert.Null(parent.Recovery);Assert.Equal("1",session.RightShift);Assert.Equal(1,session.AppliedTransform.RightShiftSamples);Assert.Equal(120,session.ViewStart);Assert.Equal(350,session.ViewEnd);Assert.Equal(120,session.SelectionStart);Assert.Equal(350,session.SelectionEnd);
            var bucket=session.DisplayWaveform.VisibleBuckets(200,201,10).Single();Assert.Equal(BitConverter.ToInt16(original,44+200*4)/32768d,bucket.LeftMin);Assert.Equal(BitConverter.ToInt16(original,44+199*4+2)/32768d,bucket.RightMin);
            Assert.Same(session,deep.GetVisualDescendants().OfType<AudioChart>().Single().Session);Assert.Equal(original,File.ReadAllBytes(session.Path!));
        }
        finally{foreach(var child in window.OwnedWindows.ToArray())child.Close();window.Close();Dispatcher.UIThread.RunJobs();}
    }
    [AvaloniaFact]public async Task DeepAnalysisFormPassesRightOffsetAndPolarityToTheActualDecoder()
    {
        using var files=new AudioDocumentTests();var session=await files.Open();byte[] original=File.ReadAllBytes(session.Path!);session.SetSelection([(100,1000)]);
        files.Dialogs.Answer=(title,fields)=>
        {
            Assert.Equal("Deeper audio analysis",title);Assert.Contains(fields,f=>f.Key=="rightShift");Assert.Contains(fields,f=>f.Key=="invertRight");
            var values=fields.ToDictionary(f=>f.Key,f=>f.Value);values["rightShift"]="3";values["invertRight"]="Yes";values["thresholds"]="1";values["inverted"]="No";return new DiskAnswer(values);
        };
        files.Shell.AudioRecoverCommand.Execute(null);await Idle(files.Coordinator);
        Assert.Null(session.Recovery);Assert.Equal("0",session.RightShift);var deep=Assert.IsType<AudioSession>(files.Coordinator.Active);Assert.NotSame(session,deep);Assert.NotNull(deep.Recovery);Assert.Equal(new AudioPcmTransform(3,true,false),deep.AppliedTransform);Assert.NotEmpty(deep.Passes);Assert.All(deep.Passes,p=>Assert.Contains("R shift +3",p.Value.AudioTransform));Assert.Equal(original,File.ReadAllBytes(session.Path!));
    }
}
