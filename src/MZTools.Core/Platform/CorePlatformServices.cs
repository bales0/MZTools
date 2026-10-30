using System;
using System.IO;
using System.Threading;

namespace MZTools;

// The host composes native file codecs and file-identity protection once. Core
// never discovers a UI assembly, loads a native library or selects an OS backend.
internal interface IAudioFileCodec
{
    IPcmAudioStreamReader OpenFlac(string path);
    void EncodeFlac(string wavPath, string outputPath, CancellationToken cancellationToken);
}

internal interface IDskRepairSaveProtection
{
    string? OriginalPath { get; }
    IDisposable Acquire(string destination);
}

internal interface IDskRepairProtectionFactory
{
    IDskRepairSaveProtection Capture(string path, byte[]? expected, IDskRepairSaveProtection? previous);
}

internal static class CorePlatformServices
{
    private sealed record Services(IAudioFileCodec Audio, IDskRepairProtectionFactory Repair);
    private static Services? services;
    private static IAudioFileCodec? audioOnly;
    internal static void ConfigureAudio(IAudioFileCodec audio)
    {ArgumentNullException.ThrowIfNull(audio);var previous=Interlocked.CompareExchange(ref audioOnly,audio,null);if(previous is not null&&!ReferenceEquals(previous,audio))throw new InvalidOperationException("Audio codec is already configured.");}

    private static IDskRepairProtectionFactory? repairOnly;

    internal static void ConfigureRepair(IDskRepairProtectionFactory repair)
    {
        ArgumentNullException.ThrowIfNull(repair);
        var previous = Interlocked.CompareExchange(ref repairOnly, repair, null);
        if (previous is not null && !ReferenceEquals(previous, repair))
            throw new InvalidOperationException("Repair protection is already configured.");
    }

    internal static void Configure(IAudioFileCodec audio, IDskRepairProtectionFactory repair)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(repair);
        if (Interlocked.CompareExchange(ref services, new(audio, repair), null) != null)
            throw new InvalidOperationException("Core platform services have already been configured.");
    }

    internal static IPcmAudioStreamReader OpenFlac(string path) =>
        (services?.Audio ?? audioOnly ?? throw new InvalidDataException("FLAC decoder is unavailable: no audio file codec was configured by the application."))
        .OpenFlac(path);

    internal static void EncodeFlac(string wavPath, string outputPath, CancellationToken token) =>
        (services?.Audio ?? audioOnly ?? throw new InvalidDataException("FLAC encoder is unavailable: no audio file codec was configured by the application."))
        .EncodeFlac(wavPath, outputPath, token);

    internal static IDskRepairSaveProtection? CaptureRepairSource(string? path, byte[]? expected = null,
        IDskRepairSaveProtection? previous = null)
    {
        // An in-memory image has no original file to protect. A file-backed image
        // must never fall back to path-only checks on an unconfigured platform.
        if (path == null) return previous;
        return (services?.Repair ?? repairOnly ?? throw new PlatformNotSupportedException(
            "Repair source identity cannot be verified on this platform; saving is blocked."))
            .Capture(path, expected, previous);
    }
}
