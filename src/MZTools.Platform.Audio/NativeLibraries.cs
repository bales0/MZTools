using System.Reflection;
using System.Runtime.InteropServices;

namespace MZTools.Platform.Audio;

internal static class NativeLibraries
{
    private static readonly object gate=new();
    private static readonly Dictionary<string,nint> loaded=[];
    static NativeLibraries()=>NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly,Resolve);
    internal static void Ensure()=>_ = typeof(NativeLibraries).TypeHandle;
    internal static void Initialize()=>System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(NativeLibraries).TypeHandle);
    internal static nint UseTestLibrary(string name,string path)
    {Initialize();lock(gate){if(loaded.ContainsKey(name))throw new InvalidOperationException("Native library already in use.");var handle=NativeLibrary.Load(Path.GetFullPath(path));loaded.Add(name,handle);return handle;}}
    private static nint Resolve(string name,Assembly assembly,DllImportSearchPath? search)
    {
        lock(gate)
        {
            if(loaded.TryGetValue(name,out var module))return module;
            string[] candidates=name switch
            {
                "mz-portaudio"=>OperatingSystem.IsMacOS()?["/opt/homebrew/lib/libportaudio.2.dylib","/usr/local/lib/libportaudio.2.dylib","libportaudio.2.dylib"]:["libportaudio.so.2"],
                "mz-sndfile"=>OperatingSystem.IsWindows()?[Path.Combine(AppContext.BaseDirectory,"runtimes","win-"+RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),"native","sndfile.dll"),Path.Combine(AppContext.BaseDirectory,"sndfile.dll")]:OperatingSystem.IsMacOS()?["/opt/homebrew/lib/libsndfile.1.dylib","/usr/local/lib/libsndfile.1.dylib","libsndfile.1.dylib"]:["libsndfile.so.1"],
                _=>[]
            };
            foreach(var path in candidates)if(NativeLibrary.TryLoad(path,out module)){loaded.Add(name,module);return module;}
            throw new DllNotFoundException($"{name} is unavailable for {RuntimeInformation.RuntimeIdentifier}. Install the matching system PortAudio/libsndfile runtime; no device was started.");
        }
    }
}
