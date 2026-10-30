using System.Numerics;
namespace MZTools.Capture;

/// <summary>Shared periodic-Hann amplitude spectrum; one instance owns bounded reusable scratch buffers.</summary>
public sealed class SpectralAnalysis
{
    private double[] hann=[];
    private Complex[][] fftScratch=[];
    private static double? Db(double amplitude)=>amplitude>0?20*Math.Log10(amplitude):null;
    public LiveSpectrumBin[] Compute(double[][] data,int available,int n,uint sampleRate,int channels,out double? leftPeak,out double? rightPeak)
    {
        if(channels is <1 or >2 || data.Length!=channels || sampleRate==0 || n is <256 or >32768 || (n&(n-1))!=0 || available<n || data.Any(c=>c.Length<available)) throw new ArgumentOutOfRangeException(nameof(n));
        if (hann.Length != n || fftScratch.Length != channels)
        {
            hann = Enumerable.Range(0,n).Select(i => .5-.5*Math.Cos(2*Math.PI*i/n)).ToArray();
            fftScratch = Enumerable.Range(0,channels).Select(_ => new Complex[n]).ToArray();
        }
        double gain = hann.Sum();
        for(int ch=0;ch<channels;ch++)
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
            double right = channels==2 ? fftScratch[1][i].Magnitude*multiplier/gain : left;
            // Promote before multiplying: N=32768 at 192/384 kHz exceeds Int32/UInt32.
            double hz = i*(double)sampleRate/n;
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
    /// <summary>Latest crossing with actual samples on each side, matching the live preview policy.</summary>
    public static int? FindCenteredCrossing(ReadOnlySpan<double> samples,int requestedFrames,double level,bool rising)
    {
        if(requestedFrames<1 || !double.IsFinite(level))throw new ArgumentOutOfRangeException(nameof(requestedFrames));
        int half=Math.Min(requestedFrames,samples.Length)/2;
        for(int i=samples.Length-half-1;i>=Math.Max(1,half);i--)
        {
            double previous=samples[i-1],current=samples[i];
            bool crossing=rising?previous<level&&current>=level:previous>level&&current<=level;
            if(crossing)return i;
        }
        return null;
    }
}
