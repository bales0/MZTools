using MZTools.Capture;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Tests;

public sealed class OperationStateTests
{
    private static DocumentCoordinator Coordinator()=>new(new TestLoader(),new TestDialogs(),new());
    private static TaskCompletionSource<bool> Gate()=>new(TaskCreationOptions.RunContinuationsAsynchronously);

    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task CompletionAndFailureReleaseLockAndAllowRetry(bool fail)
    {
        var c=Coordinator();var gate=Gate();var changes=new List<string?>();c.PropertyChanged+=(_,e)=>changes.Add(e.PropertyName);
        var task=c.ExecuteAsync(async ct=>{await gate.Task;if(fail)throw new IOException("injected");return true;},"Compressing ZX7…");
        Assert.True(c.IsWorking);Assert.True(c.CanCancel);Assert.Equal(OperationPhase.Running,c.Phase);Assert.Equal("Compressing ZX7…",c.Status);
        Assert.False(await c.ExecuteAsync(_=>Task.FromResult(true)));
        gate.SetResult(true);Assert.Equal(!fail,await task);
        Assert.False(c.IsBusy);Assert.False(c.IsWorking);Assert.False(c.CanCancel);Assert.False(c.HasProgress);
        Assert.Equal(fail?OperationPhase.Failed:OperationPhase.Completed,c.Phase);
        Assert.Contains(nameof(c.CanCancel),changes);Assert.Contains(nameof(c.Phase),changes);
        Assert.True(await c.ExecuteAsync(_=>Task.FromResult(true)));Assert.False(c.HasError);
    }

    [AvaloniaFact]public async Task InitializationNotificationsCannotReenterTheOperation()
    {
        var c=Coordinator();c.ReportError("previous error");Task<bool>? nested=null;int calls=0;
        c.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(c.Error)&&c.Error is null)nested=c.ExecuteAsync(_=>{calls++;return Task.FromResult(true);});};
        Assert.True(await c.ExecuteAsync(_=>{calls++;return Task.FromResult(true);}));
        Assert.NotNull(nested);Assert.False(await nested);Assert.Equal(1,calls);Assert.False(c.IsBusy);
    }

    [AvaloniaFact]public async Task CancellingKeepsOwnershipUntilWorkerActuallyStops()
    {
        var c=Coordinator();var gate=Gate();CancellationToken token=default;
        var task=c.ExecuteAsync(async ct=>{token=ct;await gate.Task;ct.ThrowIfCancellationRequested();return true;});
        c.Cancel();Assert.True(token.IsCancellationRequested);Assert.True(c.IsBusy);Assert.True(c.IsWorking);
        Assert.Equal(OperationPhase.Cancelling,c.Phase);Assert.False(c.CanCancel);Assert.Contains("Cancelling",c.Status);
        var idle=c.WaitForIdleAsync();Assert.False(idle.IsCompleted);gate.SetResult(true);Assert.False(await task);await idle;
        Assert.Equal(OperationPhase.Cancelled,c.Phase);Assert.False(c.IsBusy);
    }

    [AvaloniaFact]public async Task AlreadyCommittedWorkIsNotReportedAsCancelled()
    {
        var c=Coordinator();var gate=Gate();var task=c.ExecuteAsync(async _=>{await gate.Task;return true;});
        c.Cancel();gate.SetResult(true);Assert.True(await task);Assert.Equal(OperationPhase.Completed,c.Phase);Assert.Contains("Completed",c.Status);
    }

    [AvaloniaFact]public async Task DelayedProgressCannotOverwriteWaitingCancellingOrLaterGeneration()
    {
        var c=Coordinator();var gate=Gate();IProgress<WavAnalysisProgress>? old=null;
        var task=c.ExecuteAsync(async ct=>{old=c.AudioProgress();await gate.Task;return false;},"Analyzing…");
        old!.Report(new("late",.4,4,10,0));
        using(c.AwaitUserInput("Confirm output"))
        {
            Dispatcher.UIThread.RunJobs();Assert.Equal("Confirm output",c.Status);Assert.False(c.HasProgress);Assert.False(c.CanCancel);Assert.Equal(OperationPhase.AwaitingInput,c.Phase);
            using(c.AwaitUserInput("Nested choice")){Assert.Equal("Nested choice",c.Status);}
            Assert.Equal("Confirm output",c.Status);
        }
        Assert.Equal("Analyzing…",c.Status);c.Cancel();old.Report(new("late",.8,8,10,0));Dispatcher.UIThread.RunJobs();Assert.Contains("Cancelling",c.Status);
        gate.SetResult(true);await task;
        var nextGate=Gate();var next=c.ExecuteAsync(async _=>{await nextGate.Task;return true;},"New work");
        old.Report(new("stale",1,10,10,0));Dispatcher.UIThread.RunJobs();Assert.Equal("New work",c.Status);Assert.False(c.HasProgress);
        nextGate.SetResult(true);await next;old.Report(new("stale",1,10,10,0));Dispatcher.UIThread.RunJobs();Assert.False(c.HasProgress);Assert.False(c.IsWorking);
    }

    [AvaloniaTheory][InlineData("compression")][InlineData("confirmation")][InlineData("new QD")]
    public async Task ActualModalDialogsWaitWithoutWorkingAndRecoverOnClose(string kind)
    {
        var c=Coordinator();var owner=new Window();var dialogs=new DesktopDialogs(owner);var model=new ShellViewModel(c,_=>Task.CompletedTask,dialogs);owner.DataContext=model;owner.Show();
        try
        {
            var task=c.ExecuteAsync(async ct=>
            {
                if(kind=="compression")await dialogs.ChooseCompressionAsync();
                else if(kind=="confirmation")await dialogs.ConfirmOverwriteAsync("example.mzf");
                else await ((INewQuickDiskDialog)dialogs).ChooseNewQuickDiskFormatAsync();
                return false;
            });
            Dispatcher.UIThread.RunJobs();Assert.True(c.IsBusy);Assert.True(c.IsAwaitingInput);Assert.False(c.IsWorking);Assert.False(model.CancelCommand.CanExecute(null));Assert.DoesNotContain("Working",model.WpfStatus);
            Assert.Single(owner.OwnedWindows).Close();await task;
            Assert.False(c.IsBusy);Assert.False(c.IsAwaitingInput);Assert.False(c.IsWorking);
        }
        finally{foreach(var window in owner.OwnedWindows.ToArray())window.Close();owner.Close();}
    }

    [AvaloniaFact]public async Task LocalCoordinatorDoesNotChangeAnotherWindow()
    {
        var shell=Coordinator();var local=Coordinator();var gate=Gate();var task=local.ExecuteAsync(async _=>{await gate.Task;return true;});
        Assert.True(local.IsWorking);Assert.False(shell.IsWorking);Assert.Equal("Ready.",shell.Status);gate.SetResult(true);await task;
    }
    private sealed class GatedBackend : IRecorderBackendFactory,IAudioCaptureBackend
    {
        private readonly SyntheticCaptureBackend inner=new(new(){FrameCount=long.MaxValue,ChunkFramePattern=[480],ChunkInterval=TimeSpan.FromMilliseconds(10)});
        internal bool HoldEnumeration;
        internal readonly TaskCompletionSource ReleaseEnumeration=new(TaskCreationOptions.RunContinuationsAsynchronously),ReleaseStop=new(TaskCreationOptions.RunContinuationsAsynchronously),StopEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> Choices=>["Synthetic gate"];
        public IAudioCaptureBackend Create(int index,bool raw,bool engineConversion)=>this;
        public async ValueTask<IReadOnlyList<AudioCaptureDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken=default)
        {if(HoldEnumeration)await ReleaseEnumeration.Task;cancellationToken.ThrowIfCancellationRequested();return await inner.EnumerateDevicesAsync(cancellationToken);}
        public async ValueTask<IAudioCaptureSession> OpenAsync(AudioCaptureRequest request,CancellationToken cancellationToken=default)=>new GatedSession(await inner.OpenAsync(request,cancellationToken),this);
        private sealed class GatedSession(IAudioCaptureSession inner,GatedBackend owner):IAudioCaptureSession
        {
            public AudioCaptureStreamInfo Info=>inner.Info;
            public event AudioCaptureDataAvailable? DataAvailable {add=>inner.DataAvailable+=value;remove=>inner.DataAvailable-=value;}
            public event Action<AudioCaptureStopped>? Stopped {add=>inner.Stopped+=value;remove=>inner.Stopped-=value;}
            public ValueTask StartAsync(CancellationToken cancellationToken=default)=>inner.StartAsync(cancellationToken);
            public async ValueTask StopAsync(CancellationToken cancellationToken=default){owner.StopEntered.TrySetResult();await owner.ReleaseStop.Task.WaitAsync(cancellationToken);await inner.StopAsync(cancellationToken);}
            public ValueTask DisposeAsync()=>inner.DisposeAsync();
        }
    }
    [AvaloniaFact]public async Task RecorderEnumerationCancellationKeepsItsOwnLockUntilCompletion()
    {
        var factory=new GatedBackend{HoldEnumeration=true};await using var model=new RecorderModel(factory);
        var task=model.EnumerateAsync();Assert.True(model.IsBusy);Assert.True(model.CanCancelOperation);model.Cancel();
        Assert.True(model.IsBusy);Assert.False(model.CanCancelOperation);Assert.Contains("Cancelling",model.Status);
        factory.ReleaseEnumeration.SetResult();await task;Assert.False(model.IsBusy);Assert.False(model.ShowOperationCancel);
    }
    [AvaloniaFact]public async Task RecorderFinalizeDoesNotOfferAnIneffectiveCancel()
    {
        var factory=new GatedBackend();await using var model=new RecorderModel(factory);
        await model.EnumerateAsync();model.Device=model.Devices[0];model.Format=model.Formats.First(f=>f.Channels==2);await model.StartAsync();Assert.True(model.IsActive,model.Error);
        var task=model.StopAsync();
        try
        {
            await factory.StopEntered.Task.WaitAsync(TestContext.Current.CancellationToken);Assert.True(model.IsBusy);Assert.False(model.CanCancelOperation);Assert.False(model.ShowOperationCancel);
            model.Cancel();Assert.True(model.IsBusy);
        }
        finally{factory.ReleaseStop.TrySetResult();await task;}
        Assert.False(model.IsBusy);Assert.False(model.IsActive);
    }

}
