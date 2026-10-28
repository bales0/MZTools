using System.Buffers.Binary;
using System.Numerics;

namespace MZTools.Capture;

/// <summary>Preview-only bounded PCM history. Never transforms the archive or estimates sample times from callbacks.</summary>
public sealed class LiveSignalAnalyzer(LiveSignalAnalyzerOptions? options = null) : IAudioChunkConsumer
{
    private readonly LiveSignalAnalyzerOptions options = options ?? new();
    private readonly object samplesGate = new(), snapshotGate = new();
    private AudioCaptureFormat? format;
    private double[][] history = [], levelScratch = [], waveScratch = [];
    private Complex[][] fftScratch = [];
    private double[] hann = [];
    private readonly long[] clipHold = new long[2];
    private int capacity, count, write;
    private long endFrame, discontinuities, invalid;
    private double clipThreshold = 1;
    private double denominator,positiveFullScale;
    private int padding;
    private bool disposed;
    private LivePulseOptions? requestedPulses,pulseOptions;
    private LivePulseAnalyzer? pulses;
    public void ConfigurePulses(LivePulseOptions? requested)
    {
        requested?.Validate();
        lock(samplesGate)
        {
            if(requested==requestedPulses) return;
            requestedPulses=requested;
            pulseOptions=requested==null?null:requested with { Reference=requested.Reference with { Guides=Array.AsReadOnly(requested.Reference.Guides.ToArray()) } };
            pulses=format!=null&&pulseOptions!=null?new(format,pulseOptions,endFrame):null;
            pulses?.Reset(endFrame,"Pulse settings changed; new continuity epoch");
        }
    }

    public ValueTask StartAsync(AudioCaptureStreamInfo stream,CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock(samplesGate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (format != null) throw new InvalidOperationException("Live analyzer already started.");
            if (!double.IsFinite(options.HistorySeconds) || options.HistorySeconds is < .05 or > 10 || options.MaximumHistoryBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(options));
            var f = stream.ActualFormat;
            long frames = checked((long)Math.Ceiling(f.SampleRate*options.HistorySeconds));
            if (frames > int.MaxValue || checked(frames*f.Channels*sizeof(double)) > options.MaximumHistoryBytes)
                throw new ArgumentException("Live history exceeds its memory budget.");
            format = f; capacity = (int)frames;
            denominator = Math.Pow(2,f.BitsPerSample-1); padding = f.BitsPerSample-f.ValidBitsPerSample;
            positiveFullScale = f.Representation == AudioSampleRepresentation.Float32 ? 1 : 1-Math.Pow(2,1-f.ValidBitsPerSample);
            history = Enumerable.Range(0,f.Channels).Select(_ => new double[capacity]).ToArray();
            if(pulseOptions!=null) pulses=new(f,pulseOptions);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeAsync(CapturedAudioChunk chunk,CancellationToken cancellationToken = default)
    {
        Process(chunk.Data.Span,chunk.Packet,cancellationToken);
        return ValueTask.CompletedTask;
    }
    public void Process(ReadOnlySpan<byte> data,AudioCapturePacket packet,CancellationToken cancellationToken = default)
    {
        lock(samplesGate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var f = format ?? throw new InvalidOperationException("Live analyzer is not started.");
            if (packet.FirstFrame < 0 || packet.FrameCount <= 0 || packet.FirstFrame > long.MaxValue-packet.FrameCount ||
                (long)packet.FrameCount*f.BlockAlign != data.Length) throw new InvalidDataException("Incomplete live PCM frame.");
            if (packet.FirstFrame != endFrame || packet.Flags.HasFlag(AudioCapturePacketFlags.Discontinuity))
            { if (count > 0) discontinuities++; count = 0; write = 0; pulses?.Reset(packet.FirstFrame,"Preview packet gap / discontinuity"); }
            int bytes = f.BitsPerSample/8;
            double positiveClip = Math.Min(clipThreshold,positiveFullScale);
            for(int frame=0;frame<packet.FrameCount;frame++)
            {
                if ((frame&1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                int offset = frame*f.BlockAlign;
                double left = Decode(data[offset..],f);
                double right = f.Channels == 2 ? Decode(data[(offset+bytes)..],f) : left;
                endFrame = packet.FirstFrame+frame+1;
                if (!double.IsFinite(left) || !double.IsFinite(right))
                { invalid++; discontinuities++; count = 0; write = 0; pulses?.Reset(endFrame,"Non-finite preview sample"); continue; }
                pulses?.Process(endFrame-1,left,right);
                history[0][write] = left; if (f.Channels == 2) history[1][write] = right;
                if (left <= -clipThreshold || left >= positiveClip) clipHold[0]++;
                if (f.Channels == 2 && (right <= -clipThreshold || right >= positiveClip)) clipHold[1]++;
                write = (write+1)%capacity; count = Math.Min(capacity,count+1);
            }
        }
    }

    public void ResetClippingHold(double? threshold = null)
    {
        if (threshold.HasValue && (!double.IsFinite(threshold.Value) || threshold.Value is < .01 or > 1))
            throw new ArgumentOutOfRangeException(nameof(threshold));
        lock(samplesGate) { if (threshold.HasValue) clipThreshold = threshold.Value; Array.Clear(clipHold); }
    }

    public LiveSignalSnapshot? CreateSnapshot(LiveSignalViewOptions? requested = null)
    {
        var view = requested ?? new(); Validate(view);
        lock(snapshotGate)
        {
            AudioCaptureFormat f; long first,last,waveFirst,waveLast,levelFirst,breaks,bad; int waveCount,levelCount,requestedWave;
            long[] holds; double threshold; LivePulseWindow? pulseWindow=null;
            lock(samplesGate)
            {
                if (format == null) return null;
                f = format; first = endFrame-count; last = endFrame; breaks = discontinuities; bad = invalid;
                threshold = clipThreshold; holds = (long[])clipHold.Clone();
                requestedWave = (int)Math.Min(capacity,Math.Max(1,Math.Round(view.WaveSeconds*f.SampleRate)));
                waveLast = Math.Clamp(view.WaveEndFrame ?? last,first,last);
                int waveCopy = view.TriggerEnabled && !view.WaveEndFrame.HasValue ? (int)Math.Min(capacity,2L*requestedWave) : requestedWave;
                waveFirst = Math.Max(first,waveLast-waveCopy); waveCount = (int)(waveLast-waveFirst);
                int wantedLevel = (int)Math.Min(capacity,Math.Max(view.FftSize,Math.Round(view.LevelWindowSeconds*f.SampleRate)));
                levelFirst = Math.Max(first,last-wantedLevel); levelCount = (int)(last-levelFirst);
                Ensure(ref levelScratch,f.Channels,levelCount); Ensure(ref waveScratch,f.Channels,waveCount);
                Copy(levelFirst,levelCount,levelScratch,first); Copy(waveFirst,waveCount,waveScratch,first);
                if(view.Pulses!=null) pulseWindow=pulses?.Capture(view.Pulses);
            }

            bool triggered = false;
            if (view.TriggerEnabled && !view.WaveEndFrame.HasValue && waveCount > 3)
            {
                int ch = Math.Min(view.TriggerChannel,f.Channels-1), half = Math.Min(requestedWave,waveCount)/2;
                // Search the copied latest interval only; no invented edge across a preview gap.
                for(int i=waveCount-half-1;i>=Math.Max(1,half);i--)
                {
                    double previous = waveScratch[ch][i-1], current = waveScratch[ch][i];
                    bool crossing = view.RisingTrigger ? previous < view.TriggerLevel && current >= view.TriggerLevel : previous > view.TriggerLevel && current <= view.TriggerLevel;
                    if (!crossing) continue;
                    // Keep a centered subset of the requested interval so both sides contain actual samples.
                    int from = i-half, retained = Math.Min(waveCount-from,Math.Min(requestedWave,waveCount));
                    foreach(var channel in waveScratch) Array.Copy(channel,from,channel,0,retained);
                    waveFirst += from; waveCount = retained; waveLast = waveFirst+retained; triggered = true; break;
                }
            }
            if (!triggered && waveCount > requestedWave)
            {
                int skip = waveCount-requestedWave;
                foreach(var channel in waveScratch) Array.Copy(channel,skip,channel,0,requestedWave);
                waveFirst += skip; waveCount = requestedWave;
            }
            var buckets = Envelope(waveScratch,f.Channels,waveFirst,waveCount,view.WaveBuckets);
            int levelFrames = Math.Min(levelCount,Math.Max(1,(int)Math.Round(view.LevelWindowSeconds*f.SampleRate)));
            var levels = Enumerable.Range(0,f.Channels).Select(ch => Levels(levelScratch[ch],levelCount-levelFrames,levelFrames,
                f.Channels == 1 ? "Mono" : ch == 0 ? "Left" : "Right",f,threshold,holds[ch])).ToArray();
            var spectrum = Array.Empty<LiveSpectrumBin>(); double? leftPeak = null,rightPeak = null;
            long spectrumFirst = last;
            if (levelCount >= view.FftSize)
            {
                spectrumFirst = last-view.FftSize;
                spectrum = Spectrum(levelScratch,levelCount,view.FftSize,f,out leftPeak,out rightPeak);
            }
            HeadAlignmentMeasurement? alignment=null;
            if(view.Alignment != null)
            {
                int n=spectrum.Length>0?view.FftSize:Math.Min(levelCount,view.FftSize);
                alignment=HeadAlignmentAnalyzer.Measure(levelScratch[0].AsSpan(levelCount-n,n),f.Channels==2?levelScratch[1].AsSpan(levelCount-n,n):ReadOnlySpan<double>.Empty,f,last-n,spectrum,view.Alignment);
            }
            return new(f,first,last,waveFirst,waveLast,last-levelFrames,last,spectrumFirst,last,
                Array.AsReadOnly(buckets),Array.AsReadOnly(levels),Array.AsReadOnly(spectrum),view.FftSize,
                f.SampleRate/(double)view.FftSize,triggered,breaks,bad,(long)capacity*f.Channels*sizeof(double),threshold,leftPeak,rightPeak) { Alignment=alignment,Pulses=pulseWindow?.Measure() };
        }
    }

    private void Copy(long frame,int frames,double[][] destination,long first)
    {
        if (frames == 0) return;
        int oldest = (write-count+capacity)%capacity;
        int offset = (oldest+(int)(frame-first))%capacity, firstPart = Math.Min(frames,capacity-offset);
        for(int ch=0;ch<history.Length;ch++)
        { Array.Copy(history[ch],offset,destination[ch],0,firstPart); Array.Copy(history[ch],0,destination[ch],firstPart,frames-firstPart); }
    }
    private static void Ensure(ref double[][] buffers,int channels,int frames)
    {
        if (buffers.Length == channels && (channels == 0 || buffers[0].Length >= frames)) return;
        buffers = Enumerable.Range(0,channels).Select(_ => new double[frames]).ToArray();
    }
    private double Decode(ReadOnlySpan<byte> bytes,AudioCaptureFormat f)
    {
        if (f.Representation == AudioSampleRepresentation.Float32) return BinaryPrimitives.ReadSingleLittleEndian(bytes);
        if (f.Representation == AudioSampleRepresentation.Unsigned8) return (bytes[0]-128)/128.0;
        int value = f.Representation switch {
            AudioSampleRepresentation.Signed16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            AudioSampleRepresentation.Signed24 => (bytes[0] | bytes[1]<<8 | bytes[2]<<16)<<8>>8,
            _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) };
        value = (value>>padding)<<padding;
        return value/denominator;
    }
    private static bool Clipped(double value,AudioCaptureFormat f,double threshold)
    {
        double positive = f.Representation == AudioSampleRepresentation.Float32 ? 1 : 1-Math.Pow(2,1-f.ValidBitsPerSample);
        return value <= -threshold || value >= Math.Min(threshold,positive);
    }
    private static LiveChannelLevel Levels(double[] data,int from,int frames,string name,AudioCaptureFormat f,double threshold,long held)
    {
        double sum = 0,squares = 0,peak = 0; long clipped = 0;
        double positive = Math.Min(threshold,f.Representation == AudioSampleRepresentation.Float32 ? 1 : 1-Math.Pow(2,1-f.ValidBitsPerSample));
        for(int i=from;i<from+frames;i++) { double x=data[i]; sum+=x; squares+=x*x; peak=Math.Max(peak,Math.Abs(x)); if(x <= -threshold || x >= positive) clipped++; }
        double rms = frames > 0 ? Math.Sqrt(squares/frames) : 0;
        return new(name,frames,peak,rms,frames > 0 ? sum/frames : 0,Db(peak),Db(rms),clipped,held,frames > 0 ? clipped*100.0/frames : 0);
    }
    private static double? Db(double amplitude) => amplitude > 0 ? 20*Math.Log10(amplitude) : null;
    private static LiveWaveBucket[] Envelope(double[][] data,int channels,long first,int frames,int maximum)
    {
        if (frames == 0) return [];
        int buckets = Math.Min(maximum,frames); var result = new LiveWaveBucket[buckets];
        for(int b=0;b<buckets;b++)
        {
            int start = (int)((long)b*frames/buckets),end = (int)((long)(b+1)*frames/buckets);
            double lmin=double.PositiveInfinity,lmax=double.NegativeInfinity,rmin=lmin,rmax=lmax;
            for(int i=start;i<end;i++) { double l=data[0][i],r=channels==2 ? data[1][i] : l; lmin=Math.Min(lmin,l); lmax=Math.Max(lmax,l); rmin=Math.Min(rmin,r); rmax=Math.Max(rmax,r); }
            result[b] = new(first+start,first+end,lmin,lmax,rmin,rmax);
        }
        return result;
    }

    private LiveSpectrumBin[] Spectrum(double[][] data,int available,int n,AudioCaptureFormat f,out double? leftPeak,out double? rightPeak)
    {
        if (hann.Length != n || fftScratch.Length != f.Channels)
        {
            hann = Enumerable.Range(0,n).Select(i => .5-.5*Math.Cos(2*Math.PI*i/n)).ToArray();
            fftScratch = Enumerable.Range(0,f.Channels).Select(_ => new Complex[n]).ToArray();
        }
        double gain = hann.Sum();
        for(int ch=0;ch<f.Channels;ch++)
        {
            var fft = fftScratch[ch];
            for(int i=0;i<n;i++) fft[i] = new(data[ch][available-n+i]*hann[i],0);
            Transform(fft);
        }
        var bins = new LiveSpectrumBin[n/2+1]; double bestL=0,bestR=0; leftPeak=rightPeak=null;
        for(int i=0;i<bins.Length;i++)
        {
            double multiplier = i==0 || i==n/2 ? 1 : 2;
            double left = fftScratch[0][i].Magnitude*multiplier/gain;
            double right = f.Channels==2 ? fftScratch[1][i].Magnitude*multiplier/gain : left;
            double hz = i*f.SampleRate/(double)n;
            bins[i] = new(hz,Math.Max(-160,Db(left) ?? -160),Math.Max(-160,Db(right) ?? -160));
            if (i > 0 && left > bestL && left > 1e-8) { bestL=left; leftPeak=hz; }
            if (i > 0 && right > bestR && right > 1e-8) { bestR=right; rightPeak=hz; }
        }
        return bins;
    }
    internal static void Transform(Complex[] data)
    {
        int n=data.Length;
        for(int i=1,j=0;i<n;i++) { int bit=n>>1; for(; (j&bit)!=0;bit>>=1) j^=bit; j^=bit; if(i<j) (data[i],data[j])=(data[j],data[i]); }
        for(int width=2;width<=n;width<<=1)
        {
            Complex step=Complex.FromPolarCoordinates(1,-2*Math.PI/width);
            for(int start=0;start<n;start+=width)
            {
                Complex phase=Complex.One;
                for(int i=0;i<width/2;i++) { Complex even=data[start+i],odd=phase*data[start+i+width/2]; data[start+i]=even+odd; data[start+i+width/2]=even-odd; phase*=step; }
            }
        }
    }
    private static void Validate(LiveSignalViewOptions view)
    {
        if (!double.IsFinite(view.WaveSeconds) || view.WaveSeconds is < .00001 or > 10 ||
            !double.IsFinite(view.LevelWindowSeconds) || view.LevelWindowSeconds is < .001 or > 10 ||
            view.WaveBuckets is < 1 or > 4096 || view.FftSize is < 256 or > 32768 || (view.FftSize&(view.FftSize-1)) != 0 ||
            view.TriggerChannel is < 0 or > 1 || !double.IsFinite(view.TriggerLevel)) throw new ArgumentOutOfRangeException(nameof(view));
    }
    public ValueTask DisposeAsync() { lock(samplesGate) disposed = true; return ValueTask.CompletedTask; }
}
