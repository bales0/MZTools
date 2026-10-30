using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MZTools.Desktop.Platform;

// Uses handle identity, never path spelling as a substitute for file identity.
internal sealed class RepairProtectionFactory : IDskRepairProtectionFactory
{
    internal static readonly RepairProtectionFactory Instance = new();
    internal static void EnsureConfigured() => CorePlatformServices.ConfigureRepair(Instance);
    public IDskRepairSaveProtection Capture(string path, byte[]? expected, IDskRepairSaveProtection? previous)
        => RepairSourceProtection.Capture(path, expected, previous as RepairSourceProtection);
}

internal sealed class RepairSourceProtection : IDskRepairSaveProtection
{
    private sealed record Source(string Path, Identity Id, string Hash);
    private readonly Source[] sources;
    private RepairSourceProtection(Source[] sources) => this.sources = sources;
    public string? OriginalPath => sources.LastOrDefault()?.Path;
    internal static RepairSourceProtection Capture(string path, byte[]? expected, RepairSourceProtection? previous)
    {
        path = Path.GetFullPath(path);
        using var stream = Open(path);
        var id = FileIdentity(stream.SafeFileHandle);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        if (expected is not null && hash != Convert.ToHexString(SHA256.HashData(expected)))
            throw new IOException("The repair source changed after preview. Open it again.");
        var old = previous?.sources ?? [];
        if (old.Any(s => AtomicFileTransaction.PathComparer.Equals(s.Path,path) && (s.Id != id || s.Hash != hash)))
            throw new IOException("A protected repair source changed or was replaced.");
        return old.Any(s => s.Id == id) ? previous! : new([..old,new(path,id,hash)]);
    }
    public IDisposable Acquire(string destination)
    {
        destination = Path.GetFullPath(destination);
        // A no-replace rename is required in addition to this early check.
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("A repaired copy requires a new, nonexistent destination.");
        for (string? p = destination; p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Repair Save As cannot use a symbolic link or directory alias.");
        var leases = new Leases();
        try
        {
            foreach (var source in sources)
            {
                if (AtomicFileTransaction.PathComparer.Equals(source.Path,destination))
                    throw new IOException("The repair original is protected. Choose another Save As path.");
                var stream = Open(source.Path); leases.Streams.Add(stream);
                if (FileIdentity(stream.SafeFileHandle) != source.Id || Convert.ToHexString(SHA256.HashData(stream)) != source.Hash)
                    throw new IOException("The repair original changed or was replaced. Saving is blocked.");
            }
            return leases;
        }
        catch { leases.Dispose(); throw; }
    }
    private static FileStream Open(string path) => new(path,FileMode.Open,FileAccess.Read,FileShare.Read);
    private sealed class Leases : IDisposable
    {
        internal List<FileStream> Streams { get; } = [];
        public void Dispose() { foreach (var stream in Streams) stream.Dispose(); }
    }
    private readonly record struct Identity(ulong Device, ulong File);
    private static Identity FileIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle,out var info)) throw Error();
            return new(info.Volume,((ulong)info.High << 32)|info.Low);
        }
        if (OperatingSystem.IsLinux())
        {
            // Linux UAPI statx has a fixed-size ABI. Request STATX_INO using AT_EMPTY_PATH.
            // https://man7.org/linux/man-pages/man2/statx.2.html
            if(!BitConverter.IsLittleEndian)throw new PlatformNotSupportedException("Repair identity is not verified on this architecture.");
            var data = new byte[256];
            if (statx(handle.DangerousGetHandle().ToInt32(),"",0x1000,0x100,data) != 0) throw Error();
            if ((BinaryPrimitives.ReadUInt32LittleEndian(data) & 0x100) == 0)
                throw new IOException("The filesystem did not supply a verifiable inode identity.");
            return new(((ulong)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(136)) << 32) |
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(140)),BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32)));
        }
        if(OperatingSystem.IsMacOS())
        {
            if(!Environment.Is64BitProcess||!BitConverter.IsLittleEndian)throw new PlatformNotSupportedException("macOS repair identity requires a supported 64-bit architecture.");
            // Darwin stat64: dev_t at offset 0; 64-bit inode at offset 8. See Apple xnu bsd/sys/stat.h.
            var data=new byte[256];int fd=handle.DangerousGetHandle().ToInt32();
            int result=RuntimeInformation.ProcessArchitecture==Architecture.Arm64?DarwinFstat(fd,data):DarwinFstat64(fd,data);
            if(result!=0)throw Error();return new(BinaryPrimitives.ReadUInt32LittleEndian(data),BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)));
        }
        throw new PlatformNotSupportedException("Repair file identity is not verified on this platform. Saving is blocked.");
    }
    private static IOException Error() => new("File identity could not be verified; saving is blocked.",new Win32Exception(Marshal.GetLastWin32Error()));
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;
        public uint Volume,SizeHigh,SizeLow,Links,High,Low;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation info);
    [DllImport("/usr/lib/libSystem.B.dylib",EntryPoint="fstat",SetLastError=true)]private static extern int DarwinFstat(int fd,[Out]byte[] data);
    [DllImport("/usr/lib/libSystem.B.dylib",EntryPoint="fstat64",SetLastError=true)]private static extern int DarwinFstat64(int fd,[Out]byte[] data);
    [DllImport("libc",SetLastError=true)]
    private static extern int statx(int fd,[MarshalAs(UnmanagedType.LPUTF8Str)] string path,int flags,uint mask,[Out] byte[] data);
}
