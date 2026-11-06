using System.Runtime.CompilerServices;

namespace MZTools.Core.Tests;

internal static class AudioCodecSetup
{
    [ModuleInitializer]
    internal static void Initialize() => MZTools.Platform.Audio.NativeAudioCodecs.Configure();
}
