using System.Runtime.InteropServices;

namespace MZTools.Platform.Audio;

/// <summary>System libsndfile adapter, explicitly configured by the desktop host.</summary>
public static class NativeAudioCodecs
{
    private static readonly Codec codec=new();
    public static void Configure()=>CorePlatformServices.ConfigureAudio(codec);
    public static string Version {get{NativeLibraries.Initialize();return Marshal.PtrToStringUTF8(sf_version_string())??"unknown";}}
    private sealed class Codec : IAudioFileCodec
    {
        public IPcmAudioStreamReader OpenFlac(string path)=>new Reader(path);
        public void EncodeFlac(string wavPath,string outputPath,CancellationToken cancellationToken)
        {
            NativeLibraries.Initialize();if(File.Exists(outputPath)||Directory.Exists(outputPath))throw new IOException("FLAC encoding requires a new staging destination.");
            string temporary=outputPath+".encoding-"+Guid.NewGuid().ToString("N");nint file=0;
            try
            {
                using var input=new WavPcmStreamReader(wavPath);var f=input.Format;var info=new Info{Frames=f.FrameCount,Rate=checked((int)f.SampleRate),Channels=f.Channels,Format=0x170000|(f.BitsPerSample==8?1:f.BitsPerSample==16?2:3)};
                file=Open(temporary,0x20,ref info);if(file==0)throw Error(0);var buffer=new int[8192*f.Channels];int filled=0;
                void Flush(){if(filled==0)return;cancellationToken.ThrowIfCancellationRequested();long frames=filled/f.Channels;if(sf_writef_int(file,buffer,frames)!=frames)throw Error(file);filled=0;}
                input.ReadFrames((_,left,right)=>{cancellationToken.ThrowIfCancellationRequested();buffer[filled++]=left<<8;if(f.Channels==2)buffer[filled++]=right<<8;if(filled==buffer.Length)Flush();});Flush();int status=sf_close(file);file=0;if(status!=0)throw new IOException("FLAC finalization failed.");
                cancellationToken.ThrowIfCancellationRequested();File.Move(temporary,outputPath,false);
            }
            finally{if(file!=0)sf_close(file);if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
    private sealed class Reader : IPcmAudioStreamReader
    {
        private nint file;
        public PcmAudioFormat Format {get;}
        public string SourceFormat=>"FLAC";
        internal Reader(string path)
        {
            Format=FlacStreamInfo.Read(path);NativeLibraries.Initialize();var info=new Info();file=Open(path,0x10,ref info);if(file==0)throw Error(0);
            if(info.Frames!=Format.FrameCount||info.Rate!=Format.SampleRate||info.Channels!=Format.Channels||info.Seekable==0||(info.Format&0xFF0000)!=0x170000){Dispose();throw new InvalidDataException("Native FLAC output does not match STREAMINFO or is not seekable.");}
        }
        public void ReadFrames(Action<long,int,int> consume)=>ReadFrames(0,Format.FrameCount,consume);
        public void ReadFrames(long firstFrame,long frameCount,Action<long,int,int> consume)
        {
            ObjectDisposedException.ThrowIf(file==0,this);ArgumentNullException.ThrowIfNull(consume);if(firstFrame<0||frameCount<0||firstFrame>Format.FrameCount-frameCount)throw new ArgumentOutOfRangeException(nameof(firstFrame));
            if(sf_seek(file,firstFrame,0)!=firstFrame)throw Error(file);var buffer=new int[8192*Format.Channels];long remaining=frameCount,current=firstFrame;
            while(remaining>0)
            {
                long count=sf_readf_int(file,buffer,Math.Min(remaining,8192));if(count<=0)throw new InvalidDataException("FLAC is truncated before its declared sample count.");
                for(int i=0;i<count;i++){int left=buffer[i*Format.Channels]>>8,right=Format.Channels==2?buffer[i*2+1]>>8:left;consume(current++,left,right);}remaining-=count;
            }
        }
        public void Dispose(){var current=Interlocked.Exchange(ref file,0);if(current!=0)sf_close(current);}
    }
    private static nint Open(string path,int mode,ref Info info)=>OperatingSystem.IsWindows()?sf_wchar_open(path,mode,ref info):sf_open(path,mode,ref info);
    [DllImport("mz-sndfile",CallingConvention=CallingConvention.Cdecl)]private static extern nint sf_wchar_open([MarshalAs(UnmanagedType.LPWStr)]string path,int mode,ref Info info);
    [StructLayout(LayoutKind.Sequential)]private struct Info{public long Frames;public int Rate,Channels,Format,Sections,Seekable;}
    private static IOException Error(nint file)=>new("libsndfile: "+Marshal.PtrToStringUTF8(sf_strerror(file)));
    [DllImport("mz-sndfile",CallingConvention=CallingConvention.Cdecl)]private static extern nint sf_open([MarshalAs(UnmanagedType.LPUTF8Str)]string path,int mode,ref Info info);
    [DllImport("mz-sndfile",CallingConvention=CallingConvention.Cdecl)]private static extern long sf_seek(nint file,long frames,int whence);
    [DllImport("mz-sndfile",CallingConvention=CallingConvention.Cdecl)]private static extern long sf_readf_int(nint file,[Out]int[] samples,long frames);
    [DllImport("mz-sndfile",CallingConvention=CallingConvention.Cdecl)]private static extern long sf_writef_int(nint file,int[] samples,long frames);
    [DllImport("mz-sndfile",CallingConvention=CallingConvention.Cdecl)]private static extern int sf_close(nint file);
    [DllImport("mz-sndfile",CallingConvention=CallingConvention.Cdecl)]private static extern nint sf_strerror(nint file);
    [DllImport("mz-sndfile",CallingConvention=CallingConvention.Cdecl)]private static extern nint sf_version_string();
}
