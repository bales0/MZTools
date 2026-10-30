using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MZTools;

internal enum CpmLayoutId
{
    LecDd720,
    LecHd1440,
    PersonalCpm80
}

internal enum CpmSystemStorageKind { HiddenSystemTracks, BootTrackPlusSystemFile }

internal enum CpmTransferMode
{
    Polling,
    Irq,
    Mixed
}

internal enum BootLoaderKind
{
    Unknown,
    SharpIpl23Polling,
    SharpIpl41Irq,
    SharpIpl42Polling,
    CustomVerified,
    NativeSharpPersonalCpm
}

[Flags]
internal enum CpmRuntimeVerification
{
    None = 0, StaticValidated = 1, EmulatorBootVerified = 2,
    EmulatorReadWriteVerified = 4, HardwareBootVerified = 8, HardwareReadWriteVerified = 16
}

internal sealed record CpmSystemFingerprint(CpmLayoutId Layout, DskGeometrySignature Geometry,
    CpmDpbSignature Dpb, IReadOnlyList<int> SystemPhysicalTracks, string SystemAreaSha256)
{
    internal static string HashSystemArea(DskImage image, IReadOnlyList<int> tracks)
    {
        if (tracks.Count == 0 || !tracks.SequenceEqual(tracks.Distinct().Order()))
            throw new InvalidDataException("System tracks must be nonempty, unique and in canonical physical order.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (int index in tracks)
        {
            if ((uint)index >= image.Tracks.Count || image.Tracks[index] is not { } track)
                throw new InvalidDataException($"Missing system physical track {index}.");
            writer.Write(index); writer.Write(track.Sectors.Count);
            foreach (var sector in track.Sectors)
            {
                writer.Write(sector.Cylinder); writer.Write(sector.Side);
                writer.Write(sector.SectorId); writer.Write(sector.SizeCode);
                writer.Write(sector.Data.Length); writer.Write(sector.Data);
            }
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    internal static CpmSystemFingerprint From(DskDocument source)
    {
        if (source.FileSystem is not CpmFileSystem cpm) throw new InvalidDataException("Source is not CP/M.");
        if (PersonalCpmSystemInstaller.IsPersonalLayout(source))
            throw new InvalidDataException("Native P-CP/M80 uses an IPL + PCPM.SYS file-based fingerprint, not hidden system tracks.");
        var tracks = DskDocumentFactory.GetSystemPhysicalTracks(cpm.Dpb, source.Image).ToArray();
        return new(CpmSystemProfileRegistry.IdentifyLayout(source, cpm.Dpb), DskGeometrySignature.From(source.Image),
            CpmDpbSignature.From(cpm.Dpb), tracks, HashSystemArea(source.Image, tracks));
    }
    internal bool Matches(CpmSystemFingerprint other) => Layout == other.Layout && Geometry == other.Geometry &&
        Dpb == other.Dpb && SystemPhysicalTracks.SequenceEqual(other.SystemPhysicalTracks) &&
        SystemAreaSha256.Equals(other.SystemAreaSha256, StringComparison.OrdinalIgnoreCase);
}

[Flags]
internal enum SystemBrandingPolicy
{
    None = 0,
    PreserveBootName = 1,
    PreserveSystemLogo = 2,
    PreserveDisplayedVersionStrings = 4,
    PreserveAll = PreserveBootName | PreserveSystemLogo | PreserveDisplayedVersionStrings
}

internal enum SystemPatchKind
{
    BootName,
    SystemLogo,
    DisplayedVersionString,
    DriveConfiguration,
    Custom
}

internal sealed record SystemPatchDefinition(
    string Id,
    SystemPatchKind Kind,
    int PhysicalTrack,
    int DescriptorIndex,
    int Offset,
    int Length,
    bool Inverted = false);

internal sealed record DskGeometrySignature(
    int Cylinders,
    int Sides,
    int PhysicalTracks,
    string DescriptorSha256)
{
    internal static DskGeometrySignature From(DskImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (DskImage.DskTrack? track in image.Tracks)
        {
            if (track == null)
            {
                hash.AppendData([0xFF]);
                continue;
            }

            Append(hash, track.Cylinder, track.Side, track.DefaultSizeCode, track.Gap, track.Sectors.Count);
            foreach (DskImage.DskSector sector in track.Sectors)
            {
                Append(hash, sector.Cylinder, sector.Side, sector.SectorId, sector.SizeCode,
                    sector.Data.Length, sector.FdcStatus1, sector.FdcStatus2);
            }
        }
        return new(image.TrackCount, image.SideCount, image.Tracks.Count,
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, params int[] values)
    {
        Span<byte> buffer = stackalloc byte[4];
        foreach (int value in values)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            hash.AppendData(buffer);
        }
    }
}

internal sealed record CpmDpbSignature(
    ushort Spt, byte Bsh, byte Blm, byte Exm, ushort Dsm, ushort Drm,
    byte Al0, byte Al1, ushort Cks, ushort Off, ushort BlockSize,
    bool Inverted, string PhysicalMapSha256)
{
    internal static CpmDpbSignature From(CpmDpb dpb)
    {
        string tracks = dpb.PhysicalTrackMap == null ? "linear" : string.Join(",", dpb.PhysicalTrackMap);
        string sectors = dpb.PhysicalSectorMap == null ? "linear" :
            string.Join(";", dpb.PhysicalSectorMap.Select(s => $"{s.AbsoluteTrack},{s.SectorId}"));
        string mapHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tracks + "|" + sectors)));
        return new(dpb.Spt, dpb.Bsh, dpb.Blm, dpb.Exm, dpb.Dsm, dpb.Drm,
            dpb.Al0, dpb.Al1, dpb.Cks, dpb.Off, dpb.BlockSize, dpb.Inverted, mapHash);
    }
}

internal sealed record CpmSystemProfile(
    string Id,
    string DisplayName,
    CpmLayoutId Layout,
    CpmTransferMode? TransferMode,
    DskGeometrySignature Geometry,
    CpmDpbSignature Dpb,
    BootLoaderKind BootLoader,
    IReadOnlyList<int> SystemPhysicalTracks,
    IReadOnlyList<SystemPatchDefinition> Patches,
    SystemBrandingPolicy BrandingPolicy,
    string SystemAreaSha256,
    CpmRuntimeVerification Verification,
    string? ReferenceImageSha256 = null,
    CpmSystemStorageKind Storage = CpmSystemStorageKind.HiddenSystemTracks,
    CpmFileBasedSystemFingerprint? FileBasedFingerprint = null);

internal sealed record CpmSystemTemplateDefinition(
    string Id,
    string DisplayName,
    CpmSystemFingerprint Fingerprint,
    CpmTransferMode TransferMode,
    BootLoaderKind BootLoader,
    CpmRuntimeVerification Verification,
    string? ReferenceImageSha256 = null)
{
    internal CpmLayoutId Layout => Fingerprint.Layout;
}

internal static class CpmSystemProfileRegistry
{
    private static readonly SystemPatchDefinition IplBootName =
        new("ipl-boot-name", SystemPatchKind.BootName, 1, 0, 7, 13, Inverted: true);

    internal static IReadOnlyList<CpmSystemTemplateDefinition> VerifiedTemplates { get; } =
    [
        Definition("cpm23-dd-polling", "CP/M 2.3 — 720 KiB DD polling", CpmLayoutId.LecDd720,
            CpmTransferMode.Polling, BootLoaderKind.SharpIpl23Polling,
            "EEE747DC38535E4E773BB12460D817FA64EE25073DACA2B8C5335EEAA289C46A",
            "76FC96CDABEB4C961A2E2484B53641BBA36B88F59BDFEA016C38D6529740ED93"),
        Definition("cpm41-dd-irq", "CP/M 4.1 — 720 KiB DD IRQ", CpmLayoutId.LecDd720,
            CpmTransferMode.Irq, BootLoaderKind.SharpIpl41Irq,
            "E6DC85B372F2231BC9E78ABD8A9716C929544D8F54683603A17E307BDD94C9A0",
            "603C462545E764801FF51D77A94334F18383E416AC6974124674F5560ECC708A"),
        Definition("cpm41-hd-irq", "CP/M 4.1 — 1.44 MiB HD IRQ", CpmLayoutId.LecHd1440,
            CpmTransferMode.Irq, BootLoaderKind.SharpIpl41Irq,
            "DD957D587C30E5A7ADDED6D2E4002A68742C4597C2EC881147868FE02820C64A",
            "2718E0BB11C195A107CA02D91442975B8323650059F90D5D52A2AAC6577E5050"),
        Definition("cpm42-dd-polling", "CP/M 4.2 — 720 KiB DD polling", CpmLayoutId.LecDd720,
            CpmTransferMode.Polling, BootLoaderKind.SharpIpl42Polling,
            "6D541F101492AD91417D9E093D16D76B67536B56BCAD31ED686283FBECA8630F",
            "EF7CB4198301B99605527311CE705312941900C4D094EE9460BF99C79449F1AF"),
        Definition("cpm42-hd-polling", "CP/M 4.2 — 1.44 MiB HD polling", CpmLayoutId.LecHd1440,
            CpmTransferMode.Polling, BootLoaderKind.SharpIpl42Polling,
            "133EE9D7EF0796BBD5FF362B20064B02875EC7BD09278B7653644BE3E856EAED",
            "C274EF81A2AB4D75F8D146681B8BFC1817A532A604D48819BE70A1453A2565B1")
    ];

    // Frozen layout signatures from the five SHA-checked reference images.
    private static CpmSystemTemplateDefinition Definition(string id, string name, CpmLayoutId layout,
        CpmTransferMode transfer, BootLoaderKind loader, string areaHash, string referenceHash)
    {
        bool hd = layout == CpmLayoutId.LecHd1440;
        var geometry = new DskGeometrySignature(80, 2, 160, hd
            ? "1F6C2A8426AA6F194EE7E2DE07438A2F700E0B1BBCBE0AFB2D58D6C9EFA37698"
            : "13AB60FD25981D9BD23D0E4DF8347CF39CFE4ABFD901DF666713BE4A9609476E");
        var dpb = new CpmDpbSignature((ushort)(hd ? 72 : 36), (byte)(hd ? 5 : 4), (byte)(hd ? 31 : 15),
            (byte)(hd ? 1 : 0), 350, 127, 192, 0, 32, 4, (ushort)(hd ? 4096 : 2048), false,
            "F03A108468215C4BEA202EDA06E95175749C18442D626B5EF4FE6497D9423D83");
        return new(id, name, new(layout, geometry, dpb, Array.AsReadOnly(new[] { 0, 1, 2, 3 }), areaHash),
            transfer, loader, CpmRuntimeVerification.StaticValidated, referenceHash);
    }

    internal static CpmSystemProfile ResolveVerifiedSource(DskDocument source)
    {
        if (PersonalCpmSystemInstaller.IsPersonalLayout(source))
            return PersonalCpmSystemInstaller.ResolveProfile(source)
                ?? throw new InvalidDataException("Native IPL or PCPM.SYS does not match the registered P-CP/M80 system.");
        var fingerprint = CpmSystemFingerprint.From(source);
        CpmSystemTemplateDefinition definition = VerifiedTemplates.SingleOrDefault(template =>
            template.Fingerprint.Matches(fingerprint))
            ?? throw new InvalidDataException($"Source system-area SHA-256 ({fingerprint.SystemAreaSha256}) and layout do not match a registered system.");
        return CreateProfile(definition, source);
    }

    internal static CpmSystemProfile? TryResolveVerifiedSource(DskDocument source)
    {
        if (PersonalCpmSystemInstaller.IsPersonalLayout(source))
            return PersonalCpmSystemInstaller.ResolveProfile(source);
        CpmSystemFingerprint fingerprint;
        try { fingerprint = CpmSystemFingerprint.From(source); }
        catch (InvalidDataException) { return null; }
        var definition = VerifiedTemplates.SingleOrDefault(template => template.Fingerprint.Matches(fingerprint));
        return definition == null ? null : CreateProfile(definition, source);
    }

    internal static bool SupportsTarget(DskDocument? target)
    {
        if (target?.FileSystem is not CpmFileSystem cpm || target.IsReadOnly) return false;
        try
        {
            IdentifyLayout(target, cpm.Dpb);
            return DskAnalyzer.Analyze(target).Errors == 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    internal static string TargetAvailabilityReason(DskDocument? target)
    {
        if (target == null) return "No DSK document is loaded.";
        if (SupportsTarget(target))
            return "Install a registered system-area match or compatible source while preserving target files.";
        return "System installation requires a consistent writable native P-CP/M80 320 KiB, 720 KiB DD or 1.44 MiB HD CP/M target.";
    }

    internal static CpmSystemProfile CreateProfile(CpmSystemTemplateDefinition definition, DskDocument source)
    {
        if (source.FileSystem is not CpmFileSystem cpm)
            throw new InvalidDataException("A CP/M system profile requires a recognized CP/M filesystem.");
        if (source.IsReadOnly || DskAnalyzer.Analyze(source).Errors != 0)
            throw new InvalidDataException("The system template has container or filesystem errors.");

        CpmLayoutId actualLayout = IdentifyLayout(source, cpm.Dpb);
        if (actualLayout != definition.Layout)
            throw new InvalidDataException($"The registered template expects {definition.Layout}, but the source has {actualLayout} geometry/DPB.");
        if (!DskBootInfo.Inspect(source).HasSystemBytes)
            throw new InvalidDataException("The registered template contains no detectable system bytes.");

        var fingerprint = CpmSystemFingerprint.From(source);
        if (!definition.Fingerprint.Matches(fingerprint))
            throw new InvalidDataException("Source system-area SHA-256, geometry, DPB or system tracks do not match the registered fingerprint.");

        return new(definition.Id, definition.DisplayName, definition.Layout, definition.TransferMode,
            DskGeometrySignature.From(source.Image), CpmDpbSignature.From(cpm.Dpb), definition.BootLoader,
            DskDocumentFactory.GetSystemPhysicalTracks(cpm.Dpb, source.Image).ToArray(),
            [IplBootName], SystemBrandingPolicy.PreserveAll, fingerprint.SystemAreaSha256,
            definition.Verification, definition.ReferenceImageSha256);
    }

    internal static CpmLayoutId IdentifyLayout(DskDocument document, CpmDpb dpb)
    {
        if (CpmDpbSignature.From(dpb) == CpmDpbSignature.From(CpmDpb.PersonalCpm80))
        {
            PersonalCpmSystemInstaller.ValidateGeometry(document);
            return CpmLayoutId.PersonalCpm80;
        }
        if (document.Image.TrackCount == 80 && document.Image.SideCount == 2 && dpb.Off == 4 &&
            dpb.Spt == 36 && dpb.BlockSize == 2048 &&
            document.Image.Tracks.Where((_, index) => index != 1).All(t => t != null && t.Sectors.Count == 9 && t.Sectors.All(s => s.Data.Length == 512)))
            return CpmLayoutId.LecDd720;
        if (document.Image.TrackCount == 80 && document.Image.SideCount == 2 && dpb.Off == 4 &&
            dpb.Spt == 72 && dpb.BlockSize == 4096 &&
            document.Image.Tracks.Where((_, index) => index != 1).All(t => t != null && t.Sectors.Count == 18 && t.Sectors.All(s => s.Data.Length == 512)))
            return CpmLayoutId.LecHd1440;
        throw new InvalidDataException("The CP/M layout is not a registered 720 KiB DD or 1.44 MiB HD system layout.");
    }
}
