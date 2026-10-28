using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;

namespace MZTools.Capture.Windows.Tests;

public class WaveReaderIntegrationTests
{
    [Theory]
    [InlineData(AudioSampleRepresentation.Unsigned8,1,22050)]
    [InlineData(AudioSampleRepresentation.Unsigned8,2,44100)]
    [InlineData(AudioSampleRepresentation.Signed16,1,48000)]
    [InlineData(AudioSampleRepresentation.Signed16,2,88200)]
    [InlineData(AudioSampleRepresentation.Signed24,1,96000)]
    [InlineData(AudioSampleRepresentation.Signed24,2,48000)]
    [InlineData(AudioSampleRepresentation.Float32,2,48000)]
    public async Task RecordedWav_OpensInExistingReaderAndVisualAnalysisWithUnchangedPcm(AudioSampleRepresentation representation,int channels,int rate)
    {
        using var folder = new WindowsRecordingTestFolder();
        var format = new AudioCaptureFormat(rate,channels,representation);
        const int frames = 4097; // Includes odd final data sizes in PCM8/PCM24 mono.
        var backend = new SyntheticCaptureBackend(new() { FrameCount = frames,ChunkFramePattern = [1024] },(_,channel) => channel == 0 ? -.5 : .25);
        var sink = new PcmWaveRecordingSink(new(folder.PathFor("capture.wav")) { AllowPcm24Conversion = true });
        await using var session = new CaptureSession(backend,new(SyntheticCaptureBackend.DeviceId,format));
        await session.StartAsync(sink); var report = await session.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(CaptureIntegrity.Complete,report.Integrity); Assert.True(report.Recording!.AnalyzerCompatible);
        string path = Assert.Single(report.Recording.Segments).Path;
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(path));
        using(var reader = new WavPcmStreamReader(path))
        {
            Assert.Equal((uint)rate,reader.Format.SampleRate); Assert.Equal(frames,reader.Format.FrameCount);
            Assert.Equal(channels,reader.Format.Channels);
            Assert.Equal(representation == AudioSampleRepresentation.Float32 ? 24 : format.BitsPerSample,reader.Format.BitsPerSample);
            reader.ReadFrames((_,left,right) => { Assert.Equal(-4194304,left); Assert.Equal(channels == 1 ? -4194304 : 2097152,right); });
        }
        var analysis = AudioSignalAnalysisService.Analyze(path);
        Assert.Equal(frames,analysis.FrameCount); Assert.Equal((uint)rate,analysis.SampleRate); Assert.Equal(channels,analysis.ChannelCount);
        Assert.Equal(originalHash,SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task CapturedTape_ReopensAsProgramWithOriginalHeaderAndPayload()
    {
        using var folder = new WindowsRecordingTestFolder();
        byte[] body = [0,255,85,170,1,2,3,4]; byte[] mzf = new byte[128+body.Length];
        mzf[0] = 1; "LIVE TEST"u8.CopyTo(mzf.AsSpan(1)); mzf[17] = 13;
        BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(18),(ushort)body.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(20),0x1200); BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(22),0x1200); body.CopyTo(mzf,128);
        TapeRecord record;
        using(var stream = new MemoryStream(mzf)) using(var reader = new BinaryReader(stream)) record = new MZTFileReader().ReadMzfRecord(reader);
        string reference = folder.PathFor("reference.wav");
        SharpTapeExporter.Export(reference,[record],SharpTapeOutputFormat.Wav,SharpTapeMachine.Mz800);
        byte[] referenceBytes = File.ReadAllBytes(reference);
        using var source = new WavPcmStreamReader(reference);
        var format = new AudioCaptureFormat((int)source.Format.SampleRate,1,AudioSampleRepresentation.Unsigned8);
        var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
        var sink = new PcmWaveRecordingSink(new(folder.PathFor("captured.wav")));
        await using var session = new CaptureSession(backend,new("input",format),new() { MaximumChunkFrames = 32768,ArchiveQueueCapacity = 4 });
        await session.StartAsync(sink);
        long position = 0;
        while(position < source.Format.FrameCount)
        {
            int count = (int)Math.Min(32768,source.Format.FrameCount-position);
            byte[] payload = referenceBytes.AsSpan((int)(source.Format.DataOffset+position),count).ToArray();
            platform.Last!.Emit(payload,AudioClientBufferFlags.None,1000+position,position*TimeSpan.TicksPerSecond/format.SampleRate);
            position += count; long deadline = Stopwatch.GetTimestamp()+Stopwatch.Frequency*10;
            while(session.Statistics!.WrittenFrames < position)
            {
                if (Stopwatch.GetTimestamp() > deadline) throw new TimeoutException("Synthetic tape writer did not drain.");
                await Task.Yield();
            }
        }
        var report = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(CaptureIntegrity.Complete,report!.Integrity); string saved = report.Recording!.Segments[0].Path;
        var imported = Assert.Single(SharpTapeImporter.ReadFile(saved));
        Assert.Equal(record.GetSerializedHeader(),imported.GetSerializedHeader()); Assert.Equal(body,imported.Body.MzfBody);
        var analysis = AudioSignalAnalysisService.Analyze(saved);
        Assert.Equal(2,analysis.Blocks.Count); Assert.All(analysis.Blocks,b => Assert.True(b.ChecksumValid));
        Assert.Equal(source.Format.FrameCount,analysis.FrameCount); Assert.Equal(1000,report.Stream!.DeviceFrameOrigin);
        Assert.Equal(SHA256.HashData(referenceBytes),SHA256.HashData(File.ReadAllBytes(reference)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrCancellation_PreservesReadableAudioAndAnHonestSidecar(bool disconnect)
    {
        using var folder = new WindowsRecordingTestFolder(); var platform = new FakeWindowsPlatform();
        var backend = new WindowsAudioCaptureBackend(platform); var format = new AudioCaptureFormat(48000,1,AudioSampleRepresentation.Signed16);
        var sink = new PcmWaveRecordingSink(new(folder.PathFor("interrupted.wav")));
        using var cancellation = new CancellationTokenSource();
        await using var session = new CaptureSession(backend,new("input",format));
        await session.StartAsync(sink,cancellationToken:cancellation.Token);
        platform.Last!.Emit([1,2,3,4],AudioClientBufferFlags.None,77,10000);
        if (disconnect) platform.Last.End(new System.Runtime.InteropServices.COMException("Unplugged",unchecked((int)0x88890004)));
        else cancellation.Cancel();
        var report = await session.Completion!.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(disconnect ? CaptureEndReason.DeviceLost : CaptureEndReason.Cancelled,report.Reason);
        Assert.Equal(disconnect ? CaptureIntegrity.Incomplete : CaptureIntegrity.Complete,report.Integrity);
        Assert.True(report.SinkFinalized); Assert.True(report.Recording!.SidecarFinalized);
        var segment = Assert.Single(report.Recording.Segments); Assert.Equal(!disconnect,segment.Published); Assert.True(segment.PcmMatchesWrittenBytes);
        using var reader = new WavPcmStreamReader(segment.Path); Assert.Equal(2,reader.Format.FrameCount);
        Assert.Equal(new byte[] {1,2,3,4},File.ReadAllBytes(segment.Path).AsSpan(44,4).ToArray());
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(report.Recording.SidecarPath!));
        var savedSession = json.RootElement.GetProperty("session");
        Assert.Equal(report.Reason.ToString(),savedSession.GetProperty("reason").GetString());
        Assert.Equal(report.EndUtc,savedSession.GetProperty("endUtc").GetDateTimeOffset());
        Assert.Equal(77,savedSession.GetProperty("stream").GetProperty("deviceFrameOrigin").GetInt64());
    }

    [Theory]
    [InlineData(AudioSampleRepresentation.Signed16,1)]
    [InlineData(AudioSampleRepresentation.Float32,2)]
    public async Task PacketArrivalJitterAndSlowPreview_DoNotRetimePulsesOrChangeRecordedPcm(AudioSampleRepresentation representation,int channels)
    {
        using var folder = new WindowsRecordingTestFolder();
        const int rate = 48000, frames = 4097, halfPulseFrames = 24;
        var format = new AudioCaptureFormat(rate,channels,representation);
        byte[] source = new byte[frames*format.BlockAlign];
        for(int frame=0;frame<frames;frame++)
            for(int channel=0;channel<channels;channel++)
            {
                bool high = Math.Max(0,frame-channel*3)%(2*halfPulseFrames) < halfPulseFrames;
                int offset = frame*format.BlockAlign+channel*(format.BitsPerSample/8);
                if (representation == AudioSampleRepresentation.Float32)
                    BinaryPrimitives.WriteSingleLittleEndian(source.AsSpan(offset),high ? .5f : -.5f);
                else BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(offset),(short)(high ? 16384 : -16384));
            }

        var reference = await CapturePackets(jittered:false);
        var delayed = await CapturePackets(jittered:true);
        Assert.Equal(File.ReadAllBytes(reference.Recording!.Segments[0].Path),File.ReadAllBytes(delayed.Recording!.Segments[0].Path));
        Assert.Equal(reference.Recording.Segments[0].FileSha256,delayed.Recording.Segments[0].FileSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(source)),delayed.Recording.SourcePcmSha256);
        Assert.Equal(frames,delayed.Statistics.WrittenFrames); Assert.Equal(frames,delayed.Recording.SavedFrames);
        Assert.Equal(0,delayed.Statistics.KnownMissingFrames); Assert.False(delayed.Statistics.HasUnknownLoss);
        Assert.True(delayed.Statistics.AnalysisDroppedFrames > 0); Assert.True(delayed.Recording.SourceHashCoversSavedFramesExactly);

        using var reader = new WavPcmStreamReader(delayed.Recording.Segments[0].Path);
        var transitions = new List<long>(); int? previous = null;
        reader.ReadFrames((frame,left,_) => {
            Assert.Equal(frame%(2*halfPulseFrames) < halfPulseFrames ? 4194304 : -4194304,left);
            if (previous.HasValue && previous.Value != left) transitions.Add(frame);
            previous = left;
        });
        Assert.Equal(Enumerable.Range(1,(frames-1)/halfPulseFrames).Select(i => (long)i*halfPulseFrames),transitions);

        async Task<CaptureSessionReport> CapturePackets(bool jittered)
        {
            var platform = new FakeWindowsPlatform(); var backend = new WindowsAudioCaptureBackend(platform);
            var preview = jittered ? new PausedPreviewConsumer() : null;
            await using var session = new CaptureSession(backend,new("input",format));
            var sink = new PcmWaveRecordingSink(new(folder.PathFor(jittered ? "delayed.wav" : "reference.wav")) { AllowPcm24Conversion = true });
            await session.StartAsync(sink,preview);
            int[] sizes = jittered ? [1,257,31,1024,9,513,2048,71] : [frames];
            int[] delays = [0,11,0,23,1,0,7,0];
            int position = 0, packet = 0;
            try
            {
                while(position < frames)
                {
                    int count = Math.Min(sizes[packet%sizes.Length],frames-position);
                    if (jittered && delays[packet%delays.Length] > 0) await Task.Delay(delays[packet%delays.Length]);
                    platform.Last!.Emit(source.AsSpan(position*format.BlockAlign,count*format.BlockAlign).ToArray(),
                        AudioClientBufferFlags.None,1000+position,(long)position*TimeSpan.TicksPerSecond/rate);
                    if (packet == 0 && preview != null) await preview.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    position += count; packet++;
                }
            }
            finally { preview?.Release.TrySetResult(); }
            var report = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(CaptureIntegrity.Complete,report!.Integrity); Assert.True(report.SinkFinalized);
            return report;
        }
    }

    private sealed class PausedPreviewConsumer : IAudioChunkConsumer
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask StartAsync(AudioCaptureStreamInfo stream,CancellationToken token = default) => ValueTask.CompletedTask;
        public async ValueTask ConsumeAsync(CapturedAudioChunk chunk,CancellationToken token = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class WindowsRecordingTestFolder : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),"mztools-windows-capture-test-"+Guid.NewGuid().ToString("N"));
    internal WindowsRecordingTestFolder() => Directory.CreateDirectory(directory);
    internal string PathFor(string name) => Path.Combine(directory,name);
    public void Dispose() => Directory.Delete(directory,recursive:true);
}
