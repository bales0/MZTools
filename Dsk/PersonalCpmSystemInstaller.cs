using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MZTools;

internal sealed record CpmFileBasedSystemFingerprint(string BootTrackSha256, string SystemFileName,
    int User, bool SystemAttribute, long SystemFileSize, string SystemFileSha256);

// Native SHARP reads ONLY directory entry 0 for PCPM.SYS, then follows its allocation list.
internal static class PersonalCpmSystemInstaller
{
    internal const string ProfileId = "pcpm80-mz2z047-native";
    internal const string SystemFileSha256 = "E1BDED8F1F9E320F76E7732F040279850B939CF8F4EBD4D56B39FC40BB0E1C60";
    // Canonical descriptor/CHRN/length/payload hash, NOT the serialized track hash from the spec.
    internal const string BootFingerprintSha256 = "DD95944DF879A10D826FC55C1F96B6751D5937652EC45D978BD3ADB7A6F201B0";
    internal static readonly CpmFileBasedSystemFingerprint RegisteredFingerprint =
        new(BootFingerprintSha256, "PCPM.SYS", 0, true, 14848, SystemFileSha256);

    internal static bool IsPersonalLayout(DskDocument document) => document.FileSystem is CpmFileSystem cpm &&
        CpmDpbSignature.From(cpm.Dpb) == CpmDpbSignature.From(CpmDpb.PersonalCpm80);

    internal static void ValidateGeometry(DskDocument document)
    {
        if (!IsPersonalLayout(document)) throw new InvalidDataException("Native P-CP/M80 requires the exact PersonalCpm80 DPB and physical allocation map.");
        var image = document.Image;
        if (image.TrackCount != 40 || image.SideCount != 2 || image.Tracks.Count != 80)
            throw new InvalidDataException("Native P-CP/M80 requires 40 cylinders × 2 sides.");
        for (int index = 0; index < 80; index++)
        {
            var track = image.Tracks[index] ?? throw new InvalidDataException($"Missing native physical track {index}.");
            bool boot = index == 1;
            int count = boot ? 16 : 8, size = boot ? 256 : 512, n = boot ? 1 : 2;
            if (track.Cylinder != index / 2 || track.Side != index % 2 || track.DefaultSizeCode != n || track.Sectors.Count != count)
                throw new InvalidDataException($"Native track {index} has incompatible geometry.");
            for (int descriptor = 0; descriptor < count; descriptor++)
            {
                var sector = track.Sectors[descriptor];
                int id = descriptor / 2 + 1 + descriptor % 2 * (count / 2);
                if (sector.Cylinder != index / 2 || sector.Side != index % 2 || sector.SectorId != id ||
                    sector.SizeCode != n || sector.Data.Length != size || sector.FdcStatus1 != 0 || sector.FdcStatus2 != 0)
                    throw new InvalidDataException($"Native track {index}, descriptor {descriptor}: expected C/H/R/N and native interleave with no FDC errors.");
            }
        }
    }

    internal static DskFileEntry? FindSystemFile(DskDocument document) => document.FileSystem.ReadDirectory()
        .SingleOrDefault(entry => entry.User == 0 && entry.Name.Equals("PCPM", StringComparison.OrdinalIgnoreCase) &&
            entry.Extension.Equals("SYS", StringComparison.OrdinalIgnoreCase));

    internal static bool IsSystemDirectoryEntry(ReadOnlySpan<byte> entry) => entry.Length == 32 && entry[0] == 0 &&
        entry.Slice(1, 8).SequenceEqual("PCPM    "u8) && (entry[9] & 127) == 'S' && (entry[10] & 127) == 'Y' && (entry[11] & 127) == 'S';

    internal static bool IsSystemFirst(DskDocument document) => document.FileSystem is CpmFileSystem fs &&
        IsSystemDirectoryEntry(fs.ReadRawDirectoryEntry(0));

    internal static bool HasNativeLoader(DskDocument document)
    {
        if (document.Image.Tracks.Count <= 1 || document.Image.Tracks[1] is not { } boot) return false;
        byte[] logical = boot.Sectors.OrderBy(s => s.SectorId).SelectMany(s => s.Data).Select(b => (byte)(b ^ 255)).ToArray();
        if (logical.Length != 4096 || logical[0] != 3 || !logical.AsSpan(1, 6).SequenceEqual("IPLPRO"u8)) return false;
        string bytes = Encoding.ASCII.GetString(logical);
        return bytes.Contains("P-CP/M80", StringComparison.Ordinal) && bytes.Contains("PCPM    SYS", StringComparison.Ordinal) &&
            bytes.Contains("Loading P-CP/M80", StringComparison.Ordinal) && bytes.Contains("No system file", StringComparison.Ordinal) &&
            bytes.Contains("Boot error", StringComparison.Ordinal);
    }

    internal static CpmFileBasedSystemFingerprint Fingerprint(DskDocument document)
    {
        ValidateGeometry(document);
        var entry = FindSystemFile(document) ?? throw new InvalidDataException("Native IPL requires PCPM.SYS in user area 0.");
        return new(CpmSystemFingerprint.HashSystemArea(document.Image, [1]), "PCPM.SYS", entry.User,
            entry.System, entry.Size, Sha(document.FileSystem.Extract(entry)));
    }

    internal static CpmSystemProfile? ResolveProfile(DskDocument document)
    {
        try
        {
            ValidateHealthy(document);
            if (!IsSystemFirst(document)) return null;
            var fingerprint = Fingerprint(document);
            if (fingerprint != RegisteredFingerprint) return null;
            return new(ProfileId, "P-CP/M80 (MZ-2Z047) — 320 KiB", CpmLayoutId.PersonalCpm80, null,
                DskGeometrySignature.From(document.Image), CpmDpbSignature.From(((CpmFileSystem)document.FileSystem).Dpb),
                BootLoaderKind.NativeSharpPersonalCpm, [1], [], SystemBrandingPolicy.PreserveAll,
                fingerprint.BootTrackSha256, CpmRuntimeVerification.StaticValidated,
                "DA415D95139792E42D67BB0F7B0D42999EEBC73608C629B959E4B6FF58AB018E",
                CpmSystemStorageKind.BootTrackPlusSystemFile, fingerprint);
        }
        catch (InvalidDataException) { return null; }
    }

    private static void ValidateHealthy(DskDocument document)
    {
        ValidateGeometry(document);
        if (document.IsReadOnly || DskAnalyzer.Analyze(document).Errors != 0)
            throw new InvalidDataException("Native P-CP/M80 image has filesystem/container errors (Analyzer Error/Unsafe).");
        var file = FindSystemFile(document);
        if (file != null)
        {
            var fs = (CpmFileSystem)document.FileSystem;
            var entries = fs.DirectoryEntriesFor(file);
            if (entries.Count != 1 || (entries[0].Raw[12] & ~fs.Dpb.Exm) != 0 || (entries[0].Raw[14] & 63) != 0)
                throw new InvalidDataException("Native PCPM.SYS requires one consistent initial directory extent; duplicate/multiple extents are not supported.");
        }
    }

    internal static DskCompatibilityResult CheckCompatibility(DskDocument target, DskDocument source)
    {
        try
        {
            ValidateHealthy(target); ValidateHealthy(source);
            if (!HasNativeLoader(source)) throw new InvalidDataException("Source has no native P-CP/M80 loader structure; an identification header alone is insufficient.");
            var file = FindSystemFile(source);
            if (file == null || file.Size == 0 || source.FileSystem.Extract(file).LongLength != file.Size)
                throw new InvalidDataException("Source is incomplete: native IPL needs a nonempty readable PCPM.SYS in user area 0.");
            return new(true, "Native SHARP geometry/DPB compatible. Install IPL + PCPM.SYS through CP/M allocation; preserve other target files. Unknown versions remain unverified.\n" + DirectoryPlacementPolicy(target));
        }
        catch (InvalidDataException exception) { return new(false, exception.Message); }
    }

    internal static CpmSystemBuildPreflight Preflight(DskDocument target, DskDocument source,
        CpmSystemProfile profile, CpmSystemBuildOptions options)
    {
        var check = CheckCompatibility(target, source);
        string? error = !check.IsCompatible ? check.Reason : null;
        if (error == null && (profile.Layout != CpmLayoutId.PersonalCpm80 || profile.Storage != CpmSystemStorageKind.BootTrackPlusSystemFile ||
            profile.FileBasedFingerprint != Fingerprint(source) || profile.FileBasedFingerprint != RegisteredFingerprint ||
            profile.SystemAreaSha256 != BootFingerprintSha256 || !profile.SystemPhysicalTracks.SequenceEqual([1]) ||
            profile.Geometry != DskGeometrySignature.From(source.Image) || profile.Dpb != CpmDpbSignature.From(CpmDpb.PersonalCpm80) ||
            options.PreserveBranding != SystemBrandingPolicy.PreserveAll))
            error = "Source native IPL/PCPM.SYS or profile policy no longer matches the registered file-based fingerprint.";
        return error == null ? new(true, [], "Ready: native SHARP IPL + PCPM.SYS (user 0, SYS) will be installed using free allocation blocks. Other target files and metadata will be preserved. Transfer mode unverified; runtime boot not certified.\n" + check.Reason)
            : new(false, [error], "Install rejected: " + error);
    }

    internal static CpmSystemBuildResult Build(DskDocument target, DskDocument source, CpmSystemProfile? profile)
    {
        var check = CheckCompatibility(target, source);
        if (!check.IsCompatible) throw new InvalidDataException(check.Reason);
        if (profile != null)
        {
            var preflight = Preflight(target, source, profile, new());
            if (!preflight.CanBuild) throw new InvalidDataException(preflight.Report);
        }
        byte[] original = target.Serialize();
        var candidate = DskDocument.Open(original);
        var fs = (CpmFileSystem)candidate.FileSystem;
        var sourceEntry = FindSystemFile(source)!;
        byte[] payload = source.FileSystem.Extract(sourceEntry);
        for (int descriptor = 0; descriptor < 16; descriptor++)
            source.Image.Tracks[1]!.Sectors[descriptor].Data.CopyTo(candidate.Image.Tracks[1]!.Sectors[descriptor].Data, 0);
        var old = FindSystemFile(candidate);
        if (old != null) fs.Delete(old, force: true);
        int? relocatedTo = null;
        if (fs.ReadRawDirectoryEntry(0)[0] != 0xE5)
        {
            int free = fs.FindFreeDirectorySlot(1);
            if (free < 0) throw new IOException(NoRelocationSpace);
            fs.MoveDirectoryEntry(0, free);
            relocatedTo = free;
        }
        fs.Insert("PCPM.SYS", payload, user: 0);
        fs.UpdateAttributes(FindSystemFile(candidate)!, 0, sourceEntry.ReadOnly, true, sourceEntry.Archived);
        candidate.MarkModified();
        candidate = DskDocument.Open(candidate.Serialize());
        ValidateHealthy(candidate);
        if (DskGeometrySignature.From(candidate.Image) != DskGeometrySignature.From(target.Image) ||
            !payload.AsSpan().SequenceEqual(candidate.FileSystem.Extract(FindSystemFile(candidate)!)) ||
            !FindSystemFile(candidate)!.System || !IsSystemFirst(candidate))
            throw new InvalidDataException("Native installation changed geometry or failed exact PCPM.SYS verification.");
        if (Fingerprint(candidate).BootTrackSha256 != Fingerprint(source).BootTrackSha256)
            throw new InvalidDataException("Installed native IPL differs from source.");
        ValidateOtherFiles(target, candidate, relocatedTo);
        byte[] result = candidate.Serialize();
        var report = CpmSystemBuilder.CreateReport(target, source, candidate, profile, [1], original, result,
            Enumerable.Range(0, 80).ToArray()) with
        {
            Storage = CpmSystemStorageKind.BootTrackPlusSystemFile,
            SystemFileFingerprint = Fingerprint(candidate),
            SystemFileDirectorySlot = 0,
            RelocatedFirstEntrySlot = relocatedTo,
            SystemFileAllocationBlocks = ((CpmFileSystem)candidate.FileSystem).DirectoryEntriesFor(FindSystemFile(candidate)!)[0].Raw
                .Skip(16).Where(block => block != 0).Select(block => (int)block).ToArray(),
            OtherTargetFilesPreserved = true
        };
        return new(result, report);
    }

    private const string NoRelocationSpace = "P-CP/M80 requires PCPM.SYS in directory slot 0 and no free directory entry is available to relocate the existing first entry.";

    private static string DirectoryPlacementPolicy(DskDocument target)
    {
        var fs = (CpmFileSystem)target.FileSystem;
        var first = fs.ReadRawDirectoryEntry(0);
        string policy = "P-CP/M80 boot IPL requires PCPM.SYS as directory entry #0.";
        var old = FindSystemFile(target);
        bool replacingFirstSystem = old != null && fs.DirectoryEntriesFor(old).Any(e => e.Index == 0);
        if (first[0] == 0xE5 || replacingFirstSystem) return policy;
        if (first[0] > 15) throw new InvalidDataException("Directory slot 0 contains unsupported metadata; it cannot be overwritten by PCPM.SYS.");
        bool reusableSystemSlot = old != null && fs.DirectoryEntriesFor(old).Any(e => e.Index != 0);
        if (fs.FindFreeDirectorySlot(1) < 0 && !reusableSystemSlot) throw new InvalidDataException(NoRelocationSpace);
        string name = Encoding.ASCII.GetString(first, 1, 8).TrimEnd() + "." +
            new string(first.Skip(9).Take(3).Select(b => (char)(b & 127)).ToArray()).TrimEnd();
        return policy + $"\nExisting first entry {name} (user {first[0]}) will be relocated to a free directory slot.\nNo file payload will be moved unless required by normal PCPM.SYS allocation.";
    }

    private static void ValidateOtherFiles(DskDocument before, DskDocument after, int? relocatedTo)
    {
        bool IsSystem(DskFileEntry file) => file.User == 0 && file.Name.Equals("PCPM", StringComparison.OrdinalIgnoreCase) && file.Extension.Equals("SYS", StringComparison.OrdinalIgnoreCase);
        var left = before.FileSystem.ReadDirectory().Where(f => !IsSystem(f)).ToArray();
        var right = after.FileSystem.ReadDirectory().Where(f => !IsSystem(f)).ToDictionary(f => f.Key);
        if (left.Length != right.Count) throw new InvalidDataException("Native installation changed other directory files.");
        foreach (var file in left)
        {
            if (!right.TryGetValue(file.Key, out var current) || file.Size != current.Size || file.ReadOnly != current.ReadOnly ||
                file.System != current.System || file.Archived != current.Archived || file.User != current.User ||
                file.Blocks != current.Blocks || file.Extents != current.Extents ||
                !before.FileSystem.Extract(file).AsSpan().SequenceEqual(after.FileSystem.Extract(current)))
                throw new InvalidDataException($"Native installation changed target file {file.Key} or its metadata.");
        }
        // A moved extent must keep all 32 bytes, including S1/S2/RC and allocation pointers.
        var oldDirectory = ((CpmFileSystem)before.FileSystem).ReadRawDirectory();
        var newDirectory = ((CpmFileSystem)after.FileSystem).ReadRawDirectory();
        var previousSystem = FindSystemFile(before);
        var replacedSlots = previousSystem == null ? [] : ((CpmFileSystem)before.FileSystem).DirectoryEntriesFor(previousSystem).Select(e => e.Index).ToArray();
        for (int slot = 0; slot < oldDirectory.Length; slot++)
        {
            var old = oldDirectory[slot].AsSpan();
            int destination = slot == 0 && relocatedTo is int moved ? moved : slot;
            if (old[0] <= 15 && !replacedSlots.Contains(slot) && !old.SequenceEqual(newDirectory[destination]))
                throw new InvalidDataException("Native installation changed another file's directory entry/allocation list.");
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
