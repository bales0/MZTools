namespace MZTools;

internal sealed record AudioSignalPassOrigin(long PreprocessingStartSample, double ThresholdScale, double ZeroDeadband,
    string PassId, AudioPcmTransform Transform);
