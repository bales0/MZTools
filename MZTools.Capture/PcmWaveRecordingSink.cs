using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MZTools.Capture;

/// <summary>Incremental RIFF recording. Existing files are never overwritten; failed/partial audio is never deleted.</summary>
public sealed class PcmWaveRecordingSink : IAudioRecordingSink, ICaptureSessionReportSink
{
    private readonly WaveRecordingOptions options;
    private readonly IAudioRecordingStorage storage;
    private readonly List<AudioRecordingSegment> segments = [];
    private readonly List<string?> expectedPcmHashes = [];
    private readonly List<string> errors = [];
    private readonly IncrementalHash sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private AudioPcmConversion? conversion;
    private AudioCaptureStreamInfo? streamInfo;
    private AudioRecordingResult? result;
    private Stream? current;
    private IncrementalHash? currentPcmHash;
    private byte[]? conversionBuffer;
    private string? target, currentTemporary, currentTarget, sidecarTemporary;
    private long capacity, currentFirstFrame, currentFrames, nextFrame, sourceFrames, saturated, currentHashedFrames;
    private bool started, completed, disposed, damaged;
    private string? currentError;
    private string? sourceDigest;
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    public AudioRecordingResult? RecordingResult => Volatile.Read(ref result);

    public PcmWaveRecordingSink(WaveRecordingOptions options, IAudioRecordingStorage? storage = null)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentException.ThrowIfNullOrWhiteSpace(options.DestinationPath);
        if (options.ConversionBufferBytes is < 8 or > 1048576) throw new ArgumentOutOfRangeException(nameof(options.ConversionBufferBytes));
        this.options = options; this.storage = storage ?? new FileAudioRecordingStorage();
    }

    public async ValueTask StartAsync(AudioCaptureStreamInfo stream, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) throw new InvalidOperationException("Recording sink already started.");
        cancellationToken.ThrowIfCancellationRequested();
        conversion = AudioPcmQuantizer.Plan(stream.ActualFormat, options.AllowPcm24Conversion);
        if (options.RequireAnalyzerCompatibleRate && !RiffWaveLayout.AnalyzerSupportsRate(conversion.Output.SampleRate))
            throw new NotSupportedException("Choose a sample rate supported by the existing analyzer: 22050, 44100, 48000, 88200 or 96000 Hz. Recording never resamples.");
        capacity = RiffWaveLayout.FrameCapacity(conversion.Output, options.SegmentDataLimit);
        _ = RiffWaveLayout.Header(conversion.Output,0); // Validate byte rate before any file is created.
        target = Path.GetFullPath(options.DestinationPath);
        if (!string.Equals(Path.GetExtension(target),".wav",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose a WAV destination.");
        if (storage.Exists(target) || storage.Exists(target + ".json")) throw new IOException("The recording or sidecar destination already exists. Choose a new name.");
        streamInfo = stream; started = true;
        if (conversion.ChangesContainer) conversionBuffer = new byte[options.ConversionBufferBytes - options.ConversionBufferBytes % conversion.Output.BlockAlign];
        try
        {
            string temporary = TemporarySibling(target + ".json", ".partial.json");
            await using (var journal = storage.CreateNew(temporary))
            {
                sidecarTemporary = temporary;
                await JsonSerializer.SerializeAsync(journal, new { Schema = "mztools-live-capture-v1", Status = "Recording",
                    StartedUtc = DateTimeOffset.UtcNow, Stream = stream, Conversion = conversion, options.TapeNote, options.TapeSide },jsonOptions,cancellationToken).ConfigureAwait(false);
                await journal.FlushAsync(cancellationToken).ConfigureAwait(false); storage.FlushToDisk(journal);
            }
            await OpenSegmentAsync(cancellationToken).ConfigureAwait(false);
            UpdateResult();
        }
        catch (Exception error) { Remember(error); UpdateResult(); throw; }
    }

    public async ValueTask ConsumeAsync(CapturedAudioChunk chunk, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if (!started || completed || conversion == null) throw new InvalidOperationException("Recording sink is not accepting audio.");
        var packet = chunk.Packet;
        if (packet.FirstFrame != nextFrame || (long)packet.FrameCount * conversion.Source.BlockAlign != chunk.Data.Length)
            throw new InvalidDataException("Writer received a noncontiguous or misaligned PCM chunk.");
        int offset = 0;
        try
        {
            while (offset < packet.FrameCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (currentFrames == capacity)
                { await FinalizeSegmentAsync(partial:false).ConfigureAwait(false); await OpenSegmentAsync(cancellationToken).ConfigureAwait(false); }
                int bufferFrames = options.ConversionBufferBytes / Math.Max(conversion.Source.BlockAlign,conversion.Output.BlockAlign);
                int frames = (int)Math.Min(Math.Min(packet.FrameCount-offset,capacity-currentFrames),bufferFrames);
                ReadOnlyMemory<byte> raw = chunk.Data.Slice(offset*conversion.Source.BlockAlign,frames*conversion.Source.BlockAlign);
                ReadOnlyMemory<byte> output = raw;
                if (conversion.ChangesContainer)
                {
                    var counts = AudioPcmQuantizer.Convert(conversion,raw.Span,conversionBuffer!);
                    saturated += counts.SaturatedSamples;
                    output = conversionBuffer!.AsMemory(0,frames*conversion.Output.BlockAlign);
                }
                // Scope is explicitly the original buffers observed by this sink, including a block whose write may fail.
                sourceHash.AppendData(raw.Span); sourceFrames += frames;
                await current!.WriteAsync(output,cancellationToken).ConfigureAwait(false);
                currentPcmHash!.AppendData(output.Span); currentHashedFrames += frames;
                currentFrames += frames; nextFrame += frames; offset += frames;
            }
        }
        catch (Exception error) { currentError = error.Message; Remember(error); throw; }
    }

    public async ValueTask CompleteAsync(CaptureCompletion completion, CancellationToken cancellationToken = default)
    {
        if (completed) return;
        completed = true;
        damaged |= completion.Integrity == CaptureIntegrity.Incomplete;
        Exception? failure = null;
        try { if (currentTemporary != null) await FinalizeSegmentAsync(damaged || nextFrame == 0).ConfigureAwait(false); }
        catch (Exception error) { Remember(error); failure = error; }
        // No full-file reads at live segment boundaries: verification happens only after capture and archive draining stop.
        try { await VerifySegmentsAsync().ConfigureAwait(false); }
        catch (Exception error) { Remember(error); failure ??= error; }
        sourceDigest = Convert.ToHexString(sourceHash.GetCurrentHash()); UpdateResult();
        if (failure != null) throw new IOException("Recording finalization/verification failed; acquired files retained.",failure);
    }

    private async Task OpenSegmentAsync(CancellationToken token)
    {
        string destination = segments.Count == 0 ? target! : Path.Combine(Path.GetDirectoryName(target!)!,
            Path.GetFileNameWithoutExtension(target!) + "_" + (segments.Count+1).ToString("D3",System.Globalization.CultureInfo.InvariantCulture) + ".wav");
        if (storage.Exists(destination)) throw new IOException("Segment destination already exists: " + destination);
        string temporary = TemporarySibling(destination,".partial.wav");
        var stream = storage.CreateNew(temporary);
        current = stream; currentTemporary = temporary; currentTarget = destination; currentFirstFrame = nextFrame; currentFrames = 0; currentError = null;
        currentPcmHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); currentHashedFrames = 0;
        if (!stream.CanSeek || !stream.CanRead || !stream.CanWrite) throw new IOException("Recording storage must be readable, writable and seekable.");
        await stream.WriteAsync(RiffWaveLayout.Header(conversion!.Output,0),token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false); storage.FlushToDisk(stream);
    }

    private async Task FinalizeSegmentAsync(bool partial)
    {
        string path = currentTemporary!;
        string destination = currentTarget!;
        currentTemporary = null; currentTarget = null;
        Exception? failure = null;
        long frames = 0, dataBytes = 0, fileBytes = -1;
        string? trailing = null;
        bool finalized = false, published = false;
        var stream = current; current = null;
        if (stream != null)
        {
            try { await stream.FlushAsync().ConfigureAwait(false); storage.FlushToDisk(stream); }
            catch (Exception error) { failure = error; Remember(error); }
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; Remember(error); }
        }
        try
        {
            await using (var repair = storage.OpenExisting(path))
            {
                fileBytes = repair.Length;
                long stored = Math.Max(0,repair.Length-44);
                if (stored > capacity*conversion!.Output.BlockAlign) throw new InvalidDataException("Stored audio exceeds the RIFF segment boundary.");
                int align = conversion!.Output.BlockAlign;
                frames = stored/align; dataBytes = frames*align;
                int fragmentBytes = (int)(stored-dataBytes);
                if (fragmentBytes > 0)
                {
                    // Preserve every acquired byte in place. A final incomplete frame is outside the declared PCM data.
                    byte[] fragment = new byte[fragmentBytes]; repair.Position = 44+dataBytes;
                    await repair.ReadExactlyAsync(fragment).ConfigureAwait(false); trailing = Convert.ToHexString(fragment);
                    partial = true; damaged = true;
                    currentError ??= "Trailing incomplete PCM frame preserved after declared audio; see trailingPartialFrameHex.";
                }
                byte[] header = RiffWaveLayout.Header(conversion.Output,frames);
                repair.Position = 0; await repair.WriteAsync(header).ConfigureAwait(false);
                long minimumLength = 44+dataBytes+(dataBytes&1);
                if (repair.Length < minimumLength)
                { repair.Position = repair.Length; await repair.WriteAsync(new byte[(int)(minimumLength-repair.Length)]).ConfigureAwait(false); }
                await repair.FlushAsync().ConfigureAwait(false); storage.FlushToDisk(repair);
                fileBytes = repair.Length;
                repair.Position = 0; byte[] actualHeader = new byte[44]; await repair.ReadExactlyAsync(actualHeader).ConfigureAwait(false);
                if (!actualHeader.AsSpan().SequenceEqual(header)) throw new IOException("WAV header verification failed.");
                finalized = true;
            }
            if (!partial && failure == null && !damaged && frames > 0)
            { storage.MoveNew(path,destination); path = destination; published = true; }
        }
        catch (Exception error) { failure ??= error; Remember(error); }
        expectedPcmHashes.Add(currentHashedFrames == frames && currentPcmHash != null ? Convert.ToHexString(currentPcmHash.GetHashAndReset()) : null);
        currentPcmHash?.Dispose(); currentPcmHash = null;
        segments.Add(new(path,currentFirstFrame,frames,dataBytes,fileBytes,finalized,published,null,null,
            failure?.Message ?? currentError,trailing));
        UpdateResult();
        if (failure != null) throw new IOException("Recording segment could not be fully finalized; partial data retained at " + path,failure);
    }

    private async Task VerifySegmentsAsync()
    {
        Exception? failure = null;
        for(int i=0;i<segments.Count;i++)
        {
            var segment = segments[i];
            if (!segment.HeaderFinalized) continue;
            try
            {
                await using var stream = storage.OpenRead(segment.Path);
                byte[] header = new byte[44]; await stream.ReadExactlyAsync(header).ConfigureAwait(false);
                if (stream.Length != segment.FileBytes || !header.AsSpan().SequenceEqual(RiffWaveLayout.Header(conversion!.Output,segment.Frames)))
                { segments[i] = segment with { HeaderFinalized = false }; throw new IOException("A closed recording segment changed before verification: " + segment.Path); }
                var hashes = await HashFileAsync(stream,segment.DataBytes).ConfigureAwait(false);
                bool? matches = expectedPcmHashes[i] == null ? null : hashes.Pcm == expectedPcmHashes[i];
                segments[i] = segment with { FileSha256 = hashes.File, PcmSha256 = hashes.Pcm, PcmMatchesWrittenBytes = matches };
                if (matches == false) throw new IOException("Recorded PCM differs from the successfully written stream: " + segment.Path);
            }
            catch (Exception error)
            {
                Remember(error); failure ??= error;
                segments[i] = segments[i] with { Error = (segments[i].Error == null ? "" : segments[i].Error + "; ") + error.Message };
            }
        }
        if (failure != null) throw new IOException("Recording read-back verification failed; files retained.",failure);
    }

    private static async Task<(string File,string Pcm)> HashFileAsync(Stream stream,long dataBytes)
    {
        using var file = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var pcm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[131072]; stream.Position = 0; long position = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            file.AppendData(buffer,0,count);
            int from = (int)Math.Clamp(44-position,0,count);
            int to = (int)Math.Clamp(44+dataBytes-position,0,count);
            if (to > from) pcm.AppendData(buffer,from,to-from);
            position += count;
        }
        return (Convert.ToHexString(file.GetHashAndReset()),Convert.ToHexString(pcm.GetHashAndReset()));
    }

    private void Remember(Exception error)
    {
        damaged = true;
        if (errors.Count < 32) errors.Add(error.GetType().Name + ": " + error.Message);
    }

    private void UpdateResult()
    {
        if (conversion == null || sidecarTemporary == null && currentTemporary == null && segments.Count == 0) return;
        long saved = segments.Sum(s => s.Frames);
        Volatile.Write(ref result,new(conversion,saved,sourceFrames,
            sourceDigest,
            completed && sourceDigest != null && !damaged && sourceFrames == saved,saturated,Array.AsReadOnly(segments.ToArray()),
            RecordingResult?.SidecarFinalized == true ? target + ".json" : sidecarTemporary,
            RecordingResult?.SidecarFinalized == true,
            saved > 0 && segments.All(s => s.HeaderFinalized) && RiffWaveLayout.AnalyzerSupportsRate(conversion.Output.SampleRate),
            Array.AsReadOnly(errors.ToArray())));
    }

    public async ValueTask WriteSessionReportAsync(CaptureSessionReport report, CancellationToken cancellationToken = default)
    {
        if (sidecarTemporary == null || result == null) return;
        if (result.SidecarFinalized) return;
        string sidecarTarget = target + ".json";
        var planned = result with { SidecarPath = sidecarTarget, SidecarFinalized = true };
        try
        {
            await using (var file = storage.OpenExisting(sidecarTemporary))
            {
                file.Position = 0; file.SetLength(0);
                await JsonSerializer.SerializeAsync(file,new
                {
                    Schema = "mztools-live-capture-v1", Status = report.Integrity.ToString(),
                    Session = report with { Recording = null }, Recording = planned,
                    SourceHashCoverage = "Original source blocks observed by the archive sink; see sourceFramesHashed. On failure this can differ from saved/received frames.",
                    Application = new { options.ApplicationName, options.ApplicationVersion },
                    Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(), options.TapeNote, options.TapeSide
                },jsonOptions,cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false); storage.FlushToDisk(file);
            }
            storage.MoveNew(sidecarTemporary,sidecarTarget);
            Volatile.Write(ref result,planned);
        }
        catch (Exception error) { Remember(error); UpdateResult(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        try
        {
            if (!completed) await CompleteAsync(new(CaptureEndReason.Cancelled,CaptureIntegrity.Incomplete,
                new(0,0,0,0,0,0,false,0,0,0,0,0))).ConfigureAwait(false);
        }
        finally { disposed = true; sourceHash.Dispose(); conversionBuffer = null; }
    }

    private static string TemporarySibling(string destination,string suffix) => Path.Combine(Path.GetDirectoryName(destination)!,
        "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + suffix);
}
