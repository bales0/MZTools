namespace MZTools.Capture;

public sealed record LiveSignalAnalyzerOptions
{
    public double HistorySeconds { get; init; } = 2;
    public long MaximumHistoryBytes { get; init; } = 32*1024*1024;
}

public sealed record LiveSignalViewOptions
{
    public double WaveSeconds { get; init; } = .1;
    public long? WaveEndFrame { get; init; }
    public int WaveBuckets { get; init; } = 1000;
    public int FftSize { get; init; } = 4096;
    public double LevelWindowSeconds { get; init; } = .25;
    public bool TriggerEnabled { get; init; }
    public int TriggerChannel { get; init; }
    public double TriggerLevel { get; init; }
    public bool RisingTrigger { get; init; } = true;
    public HeadAlignmentOptions? Alignment { get; init; }
    public LivePulseViewOptions? Pulses { get; init; }
}

public readonly record struct LiveWaveBucket(long FirstFrame,long EndFrame,double LeftMin,double LeftMax,double RightMin,double RightMax);
public readonly record struct LiveSpectrumBin(double FrequencyHz,double LeftDbfs,double RightDbfs);
public sealed record LiveChannelLevel(string Channel,long Samples,double Peak,double Rms,double Dc,
    double? PeakDbfs,double? RmsDbfs,long ClippedSamples,long ClippingHold,double ClippingPercent);
public sealed record LiveSignalSnapshot(AudioCaptureFormat Format,long HistoryStartFrame,long HistoryEndFrame,
    long WaveStartFrame,long WaveEndFrame,long LevelStartFrame,long LevelEndFrame,long SpectrumStartFrame,long SpectrumEndFrame,
    IReadOnlyList<LiveWaveBucket> Waveform,IReadOnlyList<LiveChannelLevel> Levels,IReadOnlyList<LiveSpectrumBin> Spectrum,
    int FftSize,double FrequencyResolutionHz,bool Triggered,long PreviewDiscontinuities,long InvalidFrames,
    long HistoryBufferBytes,double ClipThreshold,double? LeftDominantHz,double? RightDominantHz)
{
    public HeadAlignmentMeasurement? Alignment { get; init; }
    public LivePulseSnapshot? Pulses { get; init; }
    public string SpectrumMethod => "Periodic Hann window; coherent-gain-corrected peak amplitude dBFS (not PSD); floor -160 dBFS";
}

public enum RecordedAudioAction { VisualAnalysis, HeuristicImport, ManualRecovery }
public interface IRecorderNavigation
{
    Task OpenAudioAsync(string path,RecordedAudioAction action,CancellationToken cancellationToken = default);
    void RevealFile(string path);
}
