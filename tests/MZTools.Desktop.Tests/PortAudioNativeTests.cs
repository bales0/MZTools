#if !WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using MZTools.Capture;
using MZTools.Platform.Audio;

namespace MZTools.Desktop.Tests;

// Runs the real P/Invoke/callback/stop adapter against an independently compiled C transport, never hardware.
public sealed class PortAudioNativeTests
{
    private static nint module;
    private static readonly SemaphoreSlim initialize=new(1,1);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void SetMode(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate int Count();
    private static async Task Mode(int mode)
    {
        await initialize.WaitAsync(TestContext.Current.CancellationToken);try
        {
            if(module==0)
            {
                string directory=Path.Combine(Path.GetTempPath(),"mztools-portaudio-abi-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string library=Path.Combine(directory,OperatingSystem.IsMacOS()?"fake.dylib":"fake.so");
                var start=new ProcessStartInfo("cc"){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true};foreach(var arg in new[]{"-std=c11","-shared",OperatingSystem.IsMacOS()?"-dynamiclib":"-fPIC","-pthread",Path.Combine(AppContext.BaseDirectory,"native","portaudio_fake.c"),"-o",library})start.ArgumentList.Add(arg);
                using var process=Process.Start(start)!;var errors=process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);await process.WaitForExitAsync(TestContext.Current.CancellationToken);Assert.True(process.ExitCode==0,await errors);module=NativeLibraries.UseTestLibrary("mz-portaudio",library);File.Delete(library);Directory.Delete(directory,false);
            }
            Marshal.GetDelegateForFunctionPointer<SetMode>(NativeLibrary.GetExport(module,"mz_mode"))(mode);
        }finally{initialize.Release();}
    }
    private static int OpenCount()=>Marshal.GetDelegateForFunctionPointer<Count>(NativeLibrary.GetExport(module,"mz_open_streams"))();
    private static AudioCaptureRequest Request=>new("portaudio:0",new(48000,2,AudioSampleRepresentation.Signed16));
    [Fact]public async Task EnumerationDoesNotOpenStreamAndNativeStopQuiescesCallbacks()
    {
        await Mode(0);var backend=new PortAudioBackend();var devices=await backend.EnumerateDevicesAsync(TestContext.Current.CancellationToken);Assert.Single(devices);Assert.Equal(0,OpenCount());var input=await backend.OpenAsync(Request,TestContext.Current.CancellationToken);int packets=0;input.DataAvailable+=(_,_)=>Interlocked.Increment(ref packets);await input.StartAsync(TestContext.Current.CancellationToken);await Task.Delay(30,TestContext.Current.CancellationToken);await input.StopAsync(TestContext.Current.CancellationToken);int stopped=packets;await Task.Delay(30,TestContext.Current.CancellationToken);Assert.Equal(stopped,packets);Assert.True(packets>0);await input.StopAsync(TestContext.Current.CancellationToken);await input.DisposeAsync();await input.DisposeAsync();Assert.Equal(0,OpenCount());
    }
    [Theory][InlineData(2)][InlineData(3)][InlineData(4)]public async Task NativeOpenFormatOrStartFailureReleasesEveryHandle(int mode)
    {
        await Mode(mode);await using var capture=new CaptureSession(new PortAudioBackend(),Request);await Assert.ThrowsAnyAsync<Exception>(()=>capture.StartAsync(cancellationToken:TestContext.Current.CancellationToken));await capture.DisposeAsync();Assert.Equal(0,OpenCount());
    }
    [Theory][InlineData(5)][InlineData(6)][InlineData(7)]public async Task NativeOverflowNullInputAndSpontaneousStopCannotBecomeComplete(int mode)
    {
        using var files=new AudioDocumentTests();await Mode(mode);await using var capture=new CaptureSession(new PortAudioBackend(),Request);await capture.StartAsync(new PcmWaveRecordingSink(new(files.FilePath("native.wav"))),cancellationToken:TestContext.Current.CancellationToken);var report=await capture.Completion!.WaitAsync(TimeSpan.FromSeconds(10),TestContext.Current.CancellationToken);Assert.Equal(CaptureIntegrity.Incomplete,report.Integrity);await capture.DisposeAsync();Assert.Equal(0,OpenCount());
    }
    [Fact]public async Task EmptyNativePacketDoesNotInventSamplesOrPreventValidRecording()
    {
        using var files=new AudioDocumentTests();await Mode(8);await using var capture=new CaptureSession(new PortAudioBackend(),Request);await capture.StartAsync(new PcmWaveRecordingSink(new(files.FilePath("native.wav"))),cancellationToken:TestContext.Current.CancellationToken);await Task.Delay(50,TestContext.Current.CancellationToken);var report=await capture.StopAsync(TestContext.Current.CancellationToken);Assert.Equal(CaptureIntegrity.Complete,report!.Integrity);Assert.True(report.Recording!.SourceHashCoversSavedFramesExactly);Assert.True(report.Recording.SavedFrames>0);await capture.DisposeAsync();Assert.Equal(0,OpenCount());
    }
}
#endif
