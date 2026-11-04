using MZTools.Capture;
using MZTools.Desktop.ViewModels;
using MZTools.Platform.Audio;

namespace MZTools.Desktop.Platform;

public sealed class RecorderBackendFactory : IRecorderBackendFactory
{
    public IReadOnlyList<string> Choices {get;}=OperatingSystem.IsWindows()?["Windows WASAPI Shared","Windows WASAPI Exclusive","Demo · synthetic, no hardware"]:["PortAudio · system input","Demo · synthetic, no hardware"];
    public IAudioCaptureBackend Create(int index,bool raw,bool engineConversion)
    {
        if(index==(OperatingSystem.IsWindows()?2:1))return new SyntheticCaptureBackend(new(){FrameCount=long.MaxValue,ChunkFramePattern=[480],ChunkInterval=TimeSpan.FromMilliseconds(10)});
#if WINDOWS
        if(index is 0 or 1)return new MZTools.Capture.Windows.WindowsAudioCaptureBackend(new(){Mode=index==0?MZTools.Capture.Windows.WindowsAudioCaptureMode.Shared:MZTools.Capture.Windows.WindowsAudioCaptureMode.Exclusive,RequestRaw=raw,AllowEngineFormatConversion=engineConversion});
#else
        if(index==0)return new PortAudioBackend();
#endif
        throw new ArgumentOutOfRangeException(nameof(index));
    }
}
