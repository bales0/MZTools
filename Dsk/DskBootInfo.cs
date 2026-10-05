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
            if (PersonalCpmSystemInstaller.IsPersonalLayout(document))
            {
                try
                {
                    PersonalCpmSystemInstaller.ValidateGeometry(document);
                    if (document.IsReadOnly || DskAnalyzer.Analyze(document).Errors != 0)
                        return new("No — inconsistent native filesystem", "P-CP/M80 image has filesystem/container errors");
                    if (!PersonalCpmSystemInstaller.HasNativeLoader(document))
                        return new("No — identification header only, no native loader", "None (data-only native P-CP/M80)");
                    var file = PersonalCpmSystemInstaller.FindSystemFile(document);
                    if (file == null || file.Size == 0)
                        return new("No — incomplete system; missing user 0 PCPM.SYS", "Native P-CP/M80 IPL expects PCPM.SYS");
                    if (!PersonalCpmSystemInstaller.IsSystemFirst(document))
                        return new("No — PCPM.SYS is not the first directory entry", "Native P-CP/M80 IPL reads directory slot 0 only");
                    bool known = PersonalCpmSystemInstaller.Fingerprint(document) == PersonalCpmSystemInstaller.RegisteredFingerprint;
                    return new(known ? "Yes — registered native IPL + PCPM.SYS (runtime unverified)" : "Unverified — native boot candidate",
                        known ? "P-CP/M80 (MZ-2Z047); file-based system" : "P-CP/M80-style IPL + PCPM.SYS; OS/version unverified", true);
                }
                catch (System.IO.InvalidDataException)
                {
                    return new("No — invalid native geometry/system file", "P-CP/M80 layout is inconsistent");
                }
            }
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
