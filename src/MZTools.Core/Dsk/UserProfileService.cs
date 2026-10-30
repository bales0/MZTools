using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MZTools;

internal sealed record CpmLayoutProfile(string Id, string Name, string Description, CpmDpb Dpb,
    DskGeometrySignature? Geometry = null, bool BuiltIn = false)
{
    public override string ToString() => Name + (BuiltIn ? " (built-in, read-only)" : "");
}
internal sealed record BootSystemUserProfile(string Id, string Name, string Description, string SourcePath,
    string SourceSha256, DskGeometrySignature Geometry, CpmDpbSignature Dpb,
    CpmSystemStorageKind Storage, string SystemFingerprint, string? RequiredSystemFile, int? RequiredDirectorySlot)
{
    public override string ToString() => Name;
}

internal static class UserProfileService
{
    internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MZTools", "Profiles");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static IReadOnlyList<CpmLayoutProfile> Layouts(string? directory = null)
    {
        var builtIn = new[] { CpmDpb.Dd, CpmDpb.Hd, CpmDpb.PersonalCpm80, CpmDpb.Sds400 }
            .Select((d, i) => new CpmLayoutProfile("builtin-" + i, d.Name, "Built-in Sharp layout; duplicate to edit.", d, BuiltIn: true));
        return builtIn.Concat(Load<CpmLayoutProfile>("layout", directory)).ToArray();
    }
    internal static IReadOnlyList<BootSystemUserProfile> BootProfiles(string? directory = null) => Load<BootSystemUserProfile>("boot", directory);
    private static T[] Load<T>(string kind, string? directory)
    {
        string folder = Path.Combine(directory ?? DefaultDirectory, kind);
        if (!Directory.Exists(folder)) return [];
        return Directory.GetFiles(folder, "*.json").Order().Select(p => JsonSerializer.Deserialize<T>(File.ReadAllText(p), JsonOptions)
            ?? throw new InvalidDataException("Empty profile: " + p)).ToArray();
    }
    private static string ProfilePath(string kind, string id, string? directory)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Only user profile IDs can be modified; built-in profiles are read-only.");
        return Path.Combine(directory ?? DefaultDirectory, kind, id + ".json");
    }
    private static void Write<T>(string kind, string id, T profile, string? directory)
    {
        string path = ProfilePath(kind, id, directory); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(profile, JsonOptions));
            JsonSerializer.Deserialize<T>(File.ReadAllText(temporary), JsonOptions);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void Save(CpmLayoutProfile profile, string? directory = null)
    {
        if (profile.BuiltIn) throw new InvalidDataException("Built-in Sharp profiles are read-only.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Dpb == null || profile.Dpb.Bsh is < 3 or > 7 || profile.Dpb.BlockSize != 128 << profile.Dpb.Bsh || profile.Dpb.Blm != (1 << profile.Dpb.Bsh) - 1)
            throw new InvalidDataException("Invalid layout name, BSH, BLM or BlockSize.");
        Write("layout", profile.Id, profile, directory);
    }
    internal static CpmLayoutPreview Preview(DskDocument document, CpmLayoutProfile profile)
    {
        if (profile.Geometry != null && profile.Geometry != DskGeometrySignature.From(document.Image))
            throw new InvalidDataException("Image geometry/descriptor order does not match the saved profile constraints.");
        return CpmLayoutService.Preview(document, profile.Dpb);
    }
    internal static void DeleteLayout(CpmLayoutProfile profile, string? directory = null)
    {
        if (profile.BuiltIn) throw new InvalidDataException("Built-in Sharp profiles are read-only.");
        File.Delete(ProfilePath("layout", profile.Id, directory));
    }
    internal static BootSystemUserProfile CaptureBoot(DskDocument document, string name, string description)
    {
        if (document.FilePath == null || document.IsModified || document.FileSystem is not CpmFileSystem cpm)
            throw new InvalidDataException("Save a consistent CP/M source first; a boot profile references that exact saved file.");
        var registered = CpmSystemProfileRegistry.TryResolveVerifiedSource(document);
        if (registered == null) throw new InvalidDataException("Only an identified boot/system source can be saved as a reusable profile.");
        return new(Guid.NewGuid().ToString("N"), name, description, Path.GetFullPath(document.FilePath), ImageVerificationService.Hash(document.Serialize()),
            DskGeometrySignature.From(document.Image), CpmDpbSignature.From(cpm.Dpb), registered.Storage,
            registered.SystemAreaSha256, registered.Storage == CpmSystemStorageKind.BootTrackPlusSystemFile ? "PCPM.SYS (user 0, SYS)" : null,
            registered.Storage == CpmSystemStorageKind.BootTrackPlusSystemFile ? 0 : null);
    }
    internal static void Save(BootSystemUserProfile profile, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidDataException("Profile name is required.");
        Write("boot", profile.Id, profile, directory);
    }
    internal static DskDocument ResolveBootSource(BootSystemUserProfile profile)
    {
        var source = DskDocument.Open(profile.SourcePath);
        var actual = CaptureBoot(source, profile.Name, profile.Description);
        if (actual.SourceSha256 != profile.SourceSha256 || actual.Geometry != profile.Geometry || actual.Dpb != profile.Dpb ||
            actual.Storage != profile.Storage || actual.SystemFingerprint != profile.SystemFingerprint ||
            actual.RequiredSystemFile != profile.RequiredSystemFile || actual.RequiredDirectorySlot != profile.RequiredDirectorySlot)
            throw new InvalidDataException("Boot profile source fingerprint/compatibility constraints changed. No installation performed.");
        return source;
    }
    internal static void DeleteBoot(BootSystemUserProfile profile, string? directory = null) => File.Delete(ProfilePath("boot", profile.Id, directory));
}
