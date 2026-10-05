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
    LecHd1440
}

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
    CustomVerified
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
    CpmTransferMode TransferMode,
    DskGeometrySignature Geometry,
    CpmDpbSignature Dpb,
    BootLoaderKind BootLoader,
    IReadOnlyList<int> SystemPhysicalTracks,
    IReadOnlyList<SystemPatchDefinition> Patches,
    SystemBrandingPolicy BrandingPolicy,
    string VerifiedTemplateSha256);

internal sealed record CpmSystemTemplateDefinition(
    string Id,
    string DisplayName,
    CpmLayoutId Layout,
    CpmTransferMode TransferMode,
    BootLoaderKind BootLoader,
    string Sha256);

internal static class CpmSystemProfileRegistry
{
    private static readonly SystemPatchDefinition IplBootName =
        new("ipl-boot-name", SystemPatchKind.BootName, 1, 0, 7, 13, Inverted: true);

    internal static IReadOnlyList<CpmSystemTemplateDefinition> VerifiedTemplates { get; } =
    [
        new("cpm23-dd-polling", "CP/M 2.3 — 720 KiB DD polling", CpmLayoutId.LecDd720,
            CpmTransferMode.Polling, BootLoaderKind.SharpIpl23Polling,
            "76FC96CDABEB4C961A2E2484B53641BBA36B88F59BDFEA016C38D6529740ED93"),
        new("cpm41-dd-irq", "CP/M 4.1 — 720 KiB DD IRQ", CpmLayoutId.LecDd720,
            CpmTransferMode.Irq, BootLoaderKind.SharpIpl41Irq,
            "603C462545E764801FF51D77A94334F18383E416AC6974124674F5560ECC708A"),
        new("cpm41-hd-irq", "CP/M 4.1 — 1.44 MiB HD IRQ", CpmLayoutId.LecHd1440,
            CpmTransferMode.Irq, BootLoaderKind.SharpIpl41Irq,
            "2718E0BB11C195A107CA02D91442975B8323650059F90D5D52A2AAC6577E5050"),
        new("cpm42-dd-polling", "CP/M 4.2 — 720 KiB DD polling", CpmLayoutId.LecDd720,
            CpmTransferMode.Polling, BootLoaderKind.SharpIpl42Polling,
            "EF7CB4198301B99605527311CE705312941900C4D094EE9460BF99C79449F1AF"),
        new("cpm42-hd-polling", "CP/M 4.2 — 1.44 MiB HD polling", CpmLayoutId.LecHd1440,
            CpmTransferMode.Polling, BootLoaderKind.SharpIpl42Polling,
            "C274EF81A2AB4D75F8D146681B8BFC1817A532A604D48819BE70A1453A2565B1")
    ];

    internal static CpmSystemProfile ResolveVerifiedSource(DskDocument source)
    {
        string sha256 = Convert.ToHexString(SHA256.HashData(source.Serialize()));
        CpmSystemTemplateDefinition definition = VerifiedTemplates.SingleOrDefault(template =>
            template.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The source DSK SHA-256 ({sha256}) is not a registered verified system template.");
        return CreateProfile(definition, source);
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
            return "Build from an exact SHA-256-registered CP/M system template while preserving the target data filesystem.";
        return "System Builder requires a consistent writable 720 KiB DD or 1.44 MiB HD CP/M target.";
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

        string actualSha = Convert.ToHexString(SHA256.HashData(source.Serialize()));
        if (!definition.Sha256.Equals(actualSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source bytes do not match the registered system template SHA-256.");

        return new(definition.Id, definition.DisplayName, definition.Layout, definition.TransferMode,
            DskGeometrySignature.From(source.Image), CpmDpbSignature.From(cpm.Dpb), definition.BootLoader,
            DskDocumentFactory.GetSystemPhysicalTracks(cpm.Dpb, source.Image).ToArray(),
            [IplBootName], SystemBrandingPolicy.PreserveAll, actualSha);
    }

    internal static CpmLayoutId IdentifyLayout(DskDocument document, CpmDpb dpb)
    {
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
