namespace MZTools.Capture;

/// <summary>UI-independent lifecycle coordinator. Views poll immutable snapshots at their own cadence.</summary>
public sealed class AudioRecorderViewModel : IAsyncDisposable
{
    private CaptureSession? session;
    private LiveSignalAnalyzer? analyzer;
    private LiveSignalSnapshot? live;
    private CaptureSessionReport? observed;
    private LivePulseOptions? pulseOptions,requestedPulseOptions;
    public void ConfigurePulses(LivePulseOptions? options)
    {
        options?.Validate();
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(options==requestedPulseOptions) return;
            requestedPulseOptions=options;
            pulseOptions=options==null?null:options with { Reference=options.Reference with { Guides=Array.AsReadOnly(options.Reference.Guides.ToArray()) } };
            analyzer?.ConfigurePulses(pulseOptions);
        }
    }
    private readonly List<CaptureSessionReport> recordings = [];
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource? starting;
    private TaskCompletionSource? disposal;
    private int operation,refresh;
    private bool disposed;
    public CaptureSessionState State => session?.State ?? CaptureSessionState.Idle;
    public bool IsActive => State is CaptureSessionState.Starting or CaptureSessionState.Monitoring or CaptureSessionState.Recording or CaptureSessionState.Stopping;
    public bool IsBusy => Volatile.Read(ref operation) != 0;
    public CaptureSessionSnapshot? Capture => session?.Snapshot;
    public LiveSignalSnapshot? Live => Volatile.Read(ref live);
    public CaptureSessionReport? LatestReport { get { Observe(); return session == null ? observed : session.Completion is { IsCompletedSuccessfully:true } task ? task.Result : null; } }
    public IReadOnlyList<CaptureSessionReport> Recordings { get { lock(gate) return recordings.ToArray(); } }
    public string? Error { get; private set; }

    public async Task StartAsync(IAudioCaptureBackend backend,AudioCaptureRequest request,WaveRecordingOptions? recording = null,CancellationToken token = default)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (operation != 0) throw new InvalidOperationException("A recorder operation is still running.");
            operation = 1; starting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
        token = linked.Token;
        try
        {
            Error = null;
            if (IsActive)
            {
                if (State != CaptureSessionState.Monitoring || recording == null) throw new InvalidOperationException("Stop the current capture first.");
                await session!.StopAsync(token); Observe();
            }
            if (session != null) { Observe(); await session.DisposeAsync(); }
            token.ThrowIfCancellationRequested();
            analyzer = new(); Volatile.Write(ref live,null);
            lock(gate) analyzer.ConfigurePulses(pulseOptions);
            session = new(backend,request);
            await session.StartAsync(recording == null ? null : new PcmWaveRecordingSink(recording),analyzer,token);
        }
        catch (Exception error) { Error = error.Message; Observe(); throw; }
        finally { lock(gate) { Volatile.Write(ref operation,0); starting!.TrySetResult(); } }
    }
    public async Task<CaptureSessionReport?> StopAsync(CancellationToken token = default)
    {
        var current = session;
        if (current == null) return null;
        try { var report = await current.StopAsync(token); Observe(); return report; }
        catch(Exception error) { Error=error.Message; throw; }
    }
    public async Task<LiveSignalSnapshot?> RefreshAsync(LiveSignalViewOptions view,CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref refresh,1,0) != 0) return Live;
        try
        {
            Observe(); var current = analyzer;
            if (current != null)
            {
                var snapshot = await Task.Run(() => current.CreateSnapshot(view),token);
                if (ReferenceEquals(analyzer,current)) Volatile.Write(ref live,snapshot);
            }
            return Live;
        }
        finally { Volatile.Write(ref refresh,0); }
    }
    public void ResetClippingHold(double? threshold = null) => analyzer?.ResetClippingHold(threshold);
    private void Observe()
    {
        lock(gate)
        {
        if (session?.Completion is not {IsCompletedSuccessfully:true} task || ReferenceEquals(observed,task.Result)) return;
        observed = task.Result;
        if (observed.Recording is {Segments.Count:>0})
        { recordings.Add(observed); if (recordings.Count>16) recordings.RemoveAt(0); }
        if (observed.Reason is not (CaptureEndReason.Stopped or CaptureEndReason.SourceCompleted or CaptureEndReason.Cancelled)) Error = observed.Diagnostics.LastOrDefault(d => d.Severity==CaptureDiagnosticSeverity.Error)?.Message ?? observed.Reason.ToString();
        }
    }
    public ValueTask DisposeAsync()
    {
        Task? pending;
        lock(gate)
        {
            if (disposal != null) return new(disposal.Task);
            disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposed = true; pending = starting?.Task;
        }
        _ = DisposeCoreAsync(pending);
        return new(disposal.Task);
    }
    private async Task DisposeCoreAsync(Task? pending)
    {
        try
        {
            lifetime.Cancel();
            if (pending != null) await pending;
            if (session != null) { await session.DisposeAsync(); Observe(); }
            analyzer = null; Volatile.Write(ref live,null);
            disposal!.TrySetResult();
        }
        catch(Exception error) { disposal!.TrySetException(error); }
    }
}
