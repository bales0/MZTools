using MZTools.Capture;
using MZTools.Desktop.ViewModels;
using MZTools.Platform.Audio;

namespace MZTools.Desktop.Platform;

public sealed class RecorderBackendFactory : IRecorderBackendFactory
{
    public IReadOnlyList<string> Choices {get;}=OperatingSystem.IsWindows()?["Demo · synthetic, no hardware","Windows WASAPI Shared","Windows WASAPI Exclusive"]:["Demo · synthetic, no hardware","PortAudio · system input"];
    public IAudioCaptureBackend Create(int index,bool raw,bool engineConversion)
    {
        if(index==0)return new SyntheticCaptureBackend(new(){FrameCount=long.MaxValue,ChunkFramePattern=[480],ChunkInterval=TimeSpan.FromMilliseconds(10)});
#if WINDOWS
        if(index is 1 or 2)return new MZTools.Capture.Windows.WindowsAudioCaptureBackend(new(){Mode=index==1?MZTools.Capture.Windows.WindowsAudioCaptureMode.Shared:MZTools.Capture.Windows.WindowsAudioCaptureMode.Exclusive,RequestRaw=raw,AllowEngineFormatConversion=engineConversion});
#else
        if(index==1)return new PortAudioBackend();
#endif
        throw new ArgumentOutOfRangeException(nameof(index));
    }
}
