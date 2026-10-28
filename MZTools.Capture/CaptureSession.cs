using System.Diagnostics;

namespace MZTools.Capture;

/// <summary>Reusable controller. Each accepted Start transfers ownership of fresh sink/consumer instances.
/// Backend is borrowed. Stop's token cancels waiting, never discards accepted archive data.</summary>
public sealed class CaptureSession : IAsyncDisposable
{
    private readonly object control = new();
    private readonly IAudioCaptureBackend backend;
    private readonly AudioCaptureRequest request;
    private readonly CapturePipelineOptions options;
    private CaptureRun? current;
    private volatile CaptureSessionState state;
    private bool disposed;
    private Task? disposeTask;
    public CaptureSessionState State => state;
    public Task<CaptureSessionReport>? Completion => Volatile.Read(ref current)?.Finished.Task;
    public CaptureStatistics? Statistics => Volatile.Read(ref current)?.Statistics;
    /// <summary>Bounded diagnostic snapshot; callers can poll at their own UI cadence. No UI callbacks run on capture threads.</summary>
    public CaptureSessionSnapshot Snapshot => Volatile.Read(ref current)?.Snapshot(state) ??
        new(state, null, new(0,0,0,0,0,0,false,0,0,0,0,0), Array.Empty<CaptureDiagnostic>(), 0);

    public CaptureSession(IAudioCaptureBackend backend, AudioCaptureRequest request, CapturePipelineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backend); ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Format);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeviceId);
        this.backend = backend; this.request = request; this.options = options ?? new(); this.options.Validate();
    }

    public Task StartAsync(IAudioRecordingSink? sink = null, IAudioChunkConsumer? analysis = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sink != null && ReferenceEquals(sink, analysis)) throw new ArgumentException("Archive and analysis must be distinct consumers.");
        lock (control)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current != null && !current.Finished.Task.IsCompleted) throw new InvalidOperationException("Capture is already active.");
            state = CaptureSessionState.Starting;
            var run = new CaptureRun(this, backend, request, options, sink, analysis, cancellationToken);
            Volatile.Write(ref current, run);
            _ = Task.Run(run.ExecuteAsync);
            return run.Started.Task;
        }
    }

    public async Task<CaptureSessionReport?> StopAsync(CancellationToken cancellationToken = default)
    {
        var run = Volatile.Read(ref current);
        if (run == null) return null;
        run.RequestStop(CaptureEndReason.Stopped);
        return await run.Finished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void SetState(CaptureSessionState value)
    {
        lock (control) { if (!disposed) state = value; }
    }

    public ValueTask DisposeAsync()
    {
        lock (control)
        {
            if (disposeTask != null) return new(disposeTask);
            disposed = true;
            var run = current;
            if (run != null && !run.Finished.Task.IsCompleted) state = CaptureSessionState.Stopping;
            disposeTask = DisposeCoreAsync(run); return new(disposeTask);
        }
    }

    private async Task DisposeCoreAsync(CaptureRun? run)
    {
        if (run != null)
        {
            run.RequestStop(CaptureEndReason.Stopped);
            await run.Finished.Task.ConfigureAwait(false);
        }
        state = CaptureSessionState.Disposed;
    }

    private sealed class CaptureRun(CaptureSession owner, IAudioCaptureBackend backend, AudioCaptureRequest request,
        CapturePipelineOptions options, IAudioRecordingSink? sink, IAudioChunkConsumer? analysis, CancellationToken callerToken)
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<CaptureSessionReport> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CaptureEndReason> stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CaptureDiagnostics diagnostics = new(options.MaximumDiagnostics);
        private readonly CancellationTokenSource startup = new();
        private CapturePipeline? pipeline;
        private AudioCaptureStreamInfo? streamInfo;
        private bool sinkStarted;
        private int failure = -1;
        internal CaptureStatistics Statistics => Volatile.Read(ref pipeline)?.Snapshot() ?? new(0,0,0,0,0,0,false,0,0,0,0,0);
        internal CaptureSessionSnapshot Snapshot(CaptureSessionState state) =>
            new(state, Volatile.Read(ref streamInfo), Statistics, diagnostics.Snapshot(), diagnostics.Omitted);
        private CaptureEndReason Reason
        {
            get
            {
                int code = Volatile.Read(ref failure);
                return code >= 0 ? (CaptureEndReason)code : stop.Task.IsCompletedSuccessfully ? stop.Task.Result : CaptureEndReason.Stopped;
            }
        }
        private bool Failed => Volatile.Read(ref failure) >= 0;
        private CaptureIntegrity Integrity => sink == null || (!sinkStarted && !Failed) ? CaptureIntegrity.NotRecorded :
            Failed || Statistics.WrittenFrames != Statistics.ReceivedFrames ? CaptureIntegrity.Incomplete : CaptureIntegrity.Complete;

        internal void RequestStop(CaptureEndReason reason)
        {
            // Serialize the stop decision with publication of successful startup. The
            // cancellation pump may run later than a synchronous native Start returns.
            lock(owner.control)
            {
                if (reason is not (CaptureEndReason.Stopped or CaptureEndReason.SourceCompleted or CaptureEndReason.Cancelled))
                    Interlocked.CompareExchange(ref failure, (int)reason, -1);
                stop.TrySetResult(reason);
            }
        }

        private void Error(CaptureEndReason reason, string code, Exception error)
        {
            diagnostics.Add(new(code, CaptureDiagnosticSeverity.Error, error.Message)); RequestStop(reason);
        }

        private void BackendStopped(AudioCaptureStopped ended)
        {
            if (ended.Reason is not (CaptureEndReason.Stopped or CaptureEndReason.SourceCompleted or CaptureEndReason.Cancelled))
                diagnostics.Add(new(ended.Reason == CaptureEndReason.DeviceLost ? "DEVICE_LOST" : "BACKEND_FAILED",
                    CaptureDiagnosticSeverity.Error, ended.Error ?? ended.Reason.ToString()));
            RequestStop(ended.Reason);
        }

        // Cancellation callbacks from a backend may be expensive. Never execute them on its capture callback.
        private async Task CancelStartupOnStopAsync()
        {
            var reason = await stop.Task.ConfigureAwait(false);
            if (reason != CaptureEndReason.SourceCompleted) await startup.CancelAsync().ConfigureAwait(false);
        }

        internal async Task ExecuteAsync()
        {
            DateTimeOffset startUtc = DateTimeOffset.UtcNow;
            long startClock = Stopwatch.GetTimestamp();
            IAudioCaptureSession? device = null;
            AudioCaptureStreamInfo? info = null;
            bool sinkFinalized = false, streaming = false;
            Exception? startError = null;
            var cancellationPump = CancelStartupOnStopAsync();
            using var registration = callerToken.Register(() => RequestStop(CaptureEndReason.Cancelled));
            try
            {
                startup.Token.ThrowIfCancellationRequested();
                device = await backend.OpenAsync(request, startup.Token).ConfigureAwait(false);
                info = device.Info;
                Volatile.Write(ref streamInfo, info);
                if (info.Device.Id != request.DeviceId || info.RequestedFormat != request.Format ||
                    !request.AllowFormatFallback && info.ActualFormat != request.Format)
                {
                    var error = new InvalidOperationException("Backend device or negotiated format does not match the capture request.");
                    Error(CaptureEndReason.FormatMismatch, "FORMAT_MISMATCH", error); throw error;
                }
                // Validate and allocate the bounded pools before a future sink can create an output file.
                pipeline = new(info.ActualFormat, options, sink, analysis, diagnostics, RequestStop);
                if (sink != null) { await sink.StartAsync(info, startup.Token).ConfigureAwait(false); sinkStarted = true; }
                if (analysis != null) await analysis.StartAsync(info, startup.Token).ConfigureAwait(false);
                device.DataAvailable += pipeline.Offer; device.Stopped += BackendStopped;
                await device.StartAsync(startup.Token).ConfigureAwait(false);
                if (device.Info.ActualFormat != info.ActualFormat)
                {
                    var error = new InvalidOperationException("Backend changed PCM format after sink initialization.");
                    Error(CaptureEndReason.FormatMismatch,"FORMAT_CHANGED",error); throw error;
                }
                info = device.Info; Volatile.Write(ref streamInfo,info);
                lock(owner.control)
                {
                    // A finite source may finish inside Start. Explicit stop/cancel or
                    // failure must instead finish cleanup before settling Started.
                    if (stop.Task.IsCompleted && (Failed || stop.Task.Result != CaptureEndReason.SourceCompleted))
                        throw new OperationCanceledException("Capture stopped before startup completed.");
                    streaming = true;
                    owner.SetState(stop.Task.IsCompleted ? CaptureSessionState.Stopping :
                        sink == null ? CaptureSessionState.Monitoring : CaptureSessionState.Recording);
                    Started.TrySetResult();
                }
                await stop.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (startup.IsCancellationRequested || callerToken.IsCancellationRequested || stop.Task.IsCompleted)
            { startError = error; if (!stop.Task.IsCompleted) RequestStop(CaptureEndReason.Cancelled); }
            catch (Exception error)
            { startError = error; Error(streaming ? CaptureEndReason.BackendError : CaptureEndReason.StartError, "CAPTURE_START_FAILED", error); }
            finally
            {
                if (!stop.Task.IsCompleted) RequestStop(CaptureEndReason.Stopped);
                owner.SetState(CaptureSessionState.Stopping);
                try { await cancellationPump.ConfigureAwait(false); }
                catch (Exception error) { Error(CaptureEndReason.BackendError, "START_CANCEL_FAILED", error); }
                if (device != null)
                {
                    try { await device.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) { Error(CaptureEndReason.BackendError, "CAPTURE_STOP_FAILED", error); }
                    info = device.Info; Volatile.Write(ref streamInfo,info);
                    if (pipeline != null) device.DataAvailable -= pipeline.Offer;
                    device.Stopped -= BackendStopped;
                }
                if (pipeline != null)
                {
                    try { await pipeline.DrainAsync().ConfigureAwait(false); }
                    catch (Exception error) { Error(CaptureEndReason.SinkError, "PIPELINE_DRAIN_FAILED", error); }
                }
                if (device != null)
                {
                    try { await device.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { Error(CaptureEndReason.BackendError, "CAPTURE_DISPOSE_FAILED", error); }
                }
                if (sinkStarted)
                {
                    try
                    {
                        await sink!.CompleteAsync(new(Reason, Integrity, Statistics), CancellationToken.None).ConfigureAwait(false);
                        sinkFinalized = true;
                    }
                    catch (Exception error) { Error(CaptureEndReason.SinkError, "SINK_FINALIZE_FAILED", error); }
                }
                if (sink != null)
                {
                    try { await sink.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) { Error(CaptureEndReason.SinkError, "SINK_DISPOSE_FAILED", error); }
                }
                if (analysis != null)
                {
                    try { await analysis.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        diagnostics.Add(new("ANALYSIS_DISPOSE_FAILED", sink == null ? CaptureDiagnosticSeverity.Error : CaptureDiagnosticSeverity.Warning, error.Message));
                        if (sink == null) RequestStop(CaptureEndReason.AnalysisError);
                    }
                }
                var endUtc = DateTimeOffset.UtcNow;
                var elapsed = Stopwatch.GetElapsedTime(startClock);
                CaptureSessionReport MakeReport()
                {
                    var recording = (sink as ICaptureSessionReportSink)?.RecordingResult;
                    var integrity = !Failed && recording is { SavedFrames: 0 } && Statistics.ReceivedFrames == 0
                        ? CaptureIntegrity.NotRecorded : Integrity;
                    return new(startUtc, endUtc, elapsed, info,
                        Reason, integrity, sinkFinalized, Statistics, diagnostics.Snapshot(), diagnostics.Omitted) { Recording = recording };
                }
                var report = MakeReport();
                if (sink is ICaptureSessionReportSink reporter)
                {
                    try { await reporter.WriteSessionReportAsync(report,CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) { Error(CaptureEndReason.SinkError,"SIDECAR_WRITE_FAILED",error); }
                    report = MakeReport();
                }
                var reason = Reason;
                if (!Started.Task.IsCompleted)
                {
                    if (!Failed && reason is CaptureEndReason.Cancelled or CaptureEndReason.Stopped)
                        Started.TrySetCanceled(new CancellationToken(true));
                    else Started.TrySetException(startError ?? new InvalidOperationException(reason.ToString()));
                }
                owner.SetState(Failed ? CaptureSessionState.Failed : CaptureSessionState.Completed);
                startup.Dispose();
                Finished.TrySetResult(report);
            }
        }
    }
}
