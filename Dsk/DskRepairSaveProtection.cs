using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MZTools;

// Windows file identity handles hardlinks, symlinks and directory aliases. The
// read leases deny write/delete sharing until the verified destination is committed.
internal sealed class DskRepairSaveProtection
{
    private sealed record Source(string Path, FileIdentity Identity);
    private readonly Source[] sources;
    private DskRepairSaveProtection(Source[] sources) => this.sources = sources;
    internal string? OriginalPath => sources.LastOrDefault()?.Path;

    internal static DskRepairSaveProtection Capture(string? path, byte[]? expected = null,
        DskRepairSaveProtection? previous = null)
    {
        if (path == null) return previous ?? new([]);
        string fullPath = System.IO.Path.GetFullPath(path);
        using var stream = OpenSource(fullPath);
        var identity = Identity(stream.SafeFileHandle);
        if (expected != null)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            if (!copy.ToArray().AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("The source file changed after preview. Open it again; no repair was applied.");
        }
        var existing = previous?.sources ?? [];
        if (existing.Any(s => s.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            if (existing.Any(s => s.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase) && s.Identity != identity))
                throw new IOException("A protected repair source was replaced. Saving is blocked.");
            return previous!;
        }
        return new([.. existing, new(fullPath, identity)]);
    }

    internal IDisposable Acquire(string destination)
    {
        destination = Path.GetFullPath(destination);
        var leases = new ReadLeases();
        try
        {
            foreach (var source in sources)
            {
                if (destination.Equals(source.Path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Container repair must not overwrite its original image. Choose a different Save As path.");
                var stream = OpenSource(source.Path);
                leases.Streams.Add(stream);
                if (Identity(stream.SafeFileHandle) != source.Identity)
                    throw new IOException("A protected repair source was replaced. Its identity cannot be verified; saving is blocked.");
            }
            // GetAttributes distinguishes a missing output from an unreadable or
            // dangling reparse point. Unverifiable existing destinations fail closed.
            bool exists;
            try { _ = File.GetAttributes(destination); exists = true; }
            catch (FileNotFoundException) { exists = false; }
            catch (DirectoryNotFoundException) { exists = false; }
            if (exists)
            {
                using var handle = File.OpenHandle(destination, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var identity = Identity(handle);
                if (sources.Any(s => s.Identity == identity))
                    throw new IOException("This destination refers to an original repair source through a file link. Choose a separate Save As path.");
            }
            return leases;
        }
        catch { leases.Dispose(); throw; }
    }

    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private sealed class ReadLeases : IDisposable
    {
        internal List<FileStream> Streams { get; } = [];
        public void Dispose() { foreach (var stream in Streams) stream.Dispose(); }
    }

    private readonly record struct FileIdentity(uint Volume, uint High, uint Low);
    private static FileIdentity Identity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Repair source identity cannot be verified on this platform; saving is blocked.");
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("File identity could not be verified; saving is blocked.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
