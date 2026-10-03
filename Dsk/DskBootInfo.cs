using System;
using System.Linq;

namespace MZTools;

internal sealed record DskBootInfo(string Bootable, string System, bool HasSystemBytes = false)
{
    internal string Summary => $"Bootable: {Bootable} | System: {System}";

    internal static DskBootInfo Inspect(DskDocument document)
    {
        if (document.FileSystem.Type == DskFileSystemType.SingleIpl)
            return new("Yes — recognized single-program IPL loader", "MZTools single IPL (program loader, not an operating system)");
        if (document.FileSystem.Type == DskFileSystemType.MultiIpl)
            return new("Yes — recognized multi-program IPL loader", "Multi-program IPL menu/loader (not an operating system)");

        var bootTrack = document.Image.Tracks.Count > 1 ? document.Image.Tracks[1] : null;
        var first = bootTrack?.Sectors.FirstOrDefault(s => s.SectorId == 1 && s.Data.Length == 256);
        byte[]? header = first?.Data.Select(b => (byte)(b ^ 0xFF)).ToArray();
        bool ipl = header != null && header[0] == 3 && header.AsSpan(1, 6).SequenceEqual("IPLPRO"u8);
        string name = ipl ? SharpMzEncoding.ConvertMzfNameToASCIIString(header!.AsSpan(7, 13).ToArray()).Trim() : "";
        bool NonFill(byte[] bytes, int skip = 0) => bytes.Skip(skip).Any(b => b is not (0 or 0xFF or 0xE5));

        if (document.FileSystem is CpmFileSystem cpm)
        {
            bool systemData = DskDocumentFactory.GetSystemPhysicalTracks(cpm.Dpb, document.Image)
                .Any(t => document.Image.Tracks[t]?.Sectors.Any(s => NonFill(s.Data, ipl && ReferenceEquals(s, first) ? 128 : 0)) == true);
            if (!systemData)
                return new(ipl ? "No — IPL header only, no system code detected" : "No — boot/system area is empty",
                    $"None (data-only {cpm.DisplayName})");
            return new("Unverified — boot/system bytes are present",
                $"CP/M layout: {cpm.DisplayName}; OS/version unverified" + (name.Length > 0 ? $"; IPL label: {name}" : ""), true);
        }
        bool bootData = bootTrack?.Sectors.Any(s =>
            !(document.FileSystem is FsmzFileSystem && s.SectorId == 16) &&
            NonFill(s.Data, ipl && ReferenceEquals(s, first) ? 128 : 0)) == true;
        if (!bootData)
            return new(ipl ? "No — IPL header only" : "No recognized boot code", "None detected");
        return new("Unverified — boot-area bytes present", ipl ? $"IPLPRO: {name}; system unverified" : "Unknown / not recognized");
    }
}
