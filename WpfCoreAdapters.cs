using NAudio.SoundFile;
using NAudio.Wave;
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MZTools;

internal static class WpfCoreAdapters
{
    [ModuleInitializer]
    internal static void Initialize() => CorePlatformServices.Configure(new SoundFileCodec(), new RepairProtectionFactory());

    // Test and tool hosts can force this module to initialize without constructing
    // an Application, showing a dialog or accessing an audio device.
    internal static void EnsureInitialized() { }

    private sealed class SoundFileCodec : IAudioFileCodec
    {
        public IPcmAudioStreamReader OpenFlac(string path) => new FlacPcmStreamReader(path);

        public void EncodeFlac(string wavPath, string outputPath, CancellationToken cancellationToken)
        {
            using var source = new SoundFileReader(wavPath);
            SoundFileWriter.CreateSoundFile(outputPath, new CancellableAudio(source, cancellationToken));
        }
    }

    private sealed class CancellableAudio(IWaveProvider source, CancellationToken cancellationToken) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<byte> buffer) { cancellationToken.ThrowIfCancellationRequested(); return source.Read(buffer); }
    }

    private sealed class RepairProtectionFactory : IDskRepairProtectionFactory
    {
        public IDskRepairSaveProtection Capture(string path, byte[]? expected, IDskRepairSaveProtection? previous) =>
            DskRepairSaveProtection.Capture(path, expected, (DskRepairSaveProtection?)previous);
    }
}
