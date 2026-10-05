using System.Security.Cryptography;

namespace MZTools.Tests;

public sealed class PersonalCpmReferenceFactAttribute : FactAttribute
{
    public PersonalCpmReferenceFactAttribute()
    {
        if (!File.Exists(PersonalCpmSystemTests.FullReference)) Skip = "Native mz-2z047v10a.DSK reference is not installed.";
    }
}

public sealed class PersonalCpmSystemTests
{
    internal static string FullReference => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MZTool.Tests/DSK/mz-2z047v10a.DSK"));
    internal static string MinimalReference => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../_SFD800/CPMv080/P-CPM80_MZ-2Z047_320K_MINIMAL_BOOTABLE.dsk"));
    private static DskDocument Source() => DskDocument.Open(FullReference);
    private static DskDocument Reopen(DskDocument doc) { doc.MarkModified(); return DskDocument.Open(doc.Serialize()); }
    private static DskDocument Blank() => DskDocumentFactory.CreatePersonalCpm80(false);
    private static DskFileEntry Sys(DskDocument doc) => PersonalCpmSystemInstaller.FindSystemFile(doc)!;
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public void BlankHasNativeInterleaveExactDpbAndNoInstalledSystem()
    {
        var doc = Blank();
        PersonalCpmSystemInstaller.ValidateGeometry(doc);
        Assert.Equal(40, doc.Image.TrackCount); Assert.Equal(2, doc.Image.SideCount);
        Assert.Equal(new byte[] { 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15, 8, 16 }, doc.Image.Tracks[1]!.Sectors.Select(s => s.SectorId));
        foreach (var track in doc.Image.Tracks.Where((_, i) => i != 1))
        {
            Assert.Equal(new byte[] { 1, 5, 2, 6, 3, 7, 4, 8 }, track!.Sectors.Select(s => s.SectorId));
            Assert.All(track.Sectors, s => Assert.Equal(512, s.Data.Length));
        }
        var dpb = Assert.IsType<CpmFileSystem>(doc.FileSystem).Dpb;
        Assert.Equal(CpmDpbSignature.From(CpmDpb.PersonalCpm80), CpmDpbSignature.From(dpb));
        Assert.Equal(63, dpb.Drm);
        Assert.Equal(Enumerable.Range(0, 40).Select(c => c * 2).Concat(Enumerable.Range(1, 39).Reverse().Select(c => c * 2 + 1)), dpb.PhysicalTrackMap);
        Assert.False(DskBootInfo.Inspect(doc).HasSystemBytes);
        Assert.StartsWith("No", DskBootInfo.Inspect(doc).Bootable);
        Assert.True(CpmSystemProfileRegistry.SupportsTarget(doc));
        Assert.NotEmpty(DskCapabilityService.GetAvailableBootSystems(doc));
        Assert.Equal(0, DskAnalyzer.Analyze(doc).Errors);
        Assert.Equal(Enumerable.Range(1, 16).Select(n => (byte)n), DskDocumentFactory.CreatePersonalCpm80(true).Image.Tracks[1]!.Sectors.Select(s => s.SectorId));
    }

    [PersonalCpmReferenceFact]
    public void FullReferenceRecognizedWithoutDependingOnOtherFiles()
    {
        var source = Source();
        Assert.Equal("DA415D95139792E42D67BB0F7B0D42999EEBC73608C629B959E4B6FF58AB018E", Sha(source.Serialize()));
        Assert.True(PersonalCpmSystemInstaller.HasNativeLoader(source));
        var profile = CpmSystemProfileRegistry.ResolveVerifiedSource(source);
        Assert.Equal(PersonalCpmSystemInstaller.ProfileId, profile.Id);
        Assert.Equal(CpmSystemStorageKind.BootTrackPlusSystemFile, profile.Storage);
        Assert.Null(profile.TransferMode);
        Assert.Equal(BootLoaderKind.NativeSharpPersonalCpm, profile.BootLoader);
        Assert.Equal(PersonalCpmSystemInstaller.RegisteredFingerprint, profile.FileBasedFingerprint);
        Assert.Equal(14848, source.FileSystem.Extract(Sys(source)).Length);
        Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(source));
        Assert.True(Sys(source).System);
        var first = source.FileSystem.ReadDirectory().First(f => f.Extension == "COM");
        source.FileSystem.Delete(first);
        source.FileSystem.Insert("NEWGAME.COM", new byte[2048], user: 7);
        source = Reopen(source);
        Assert.Equal(profile.Id, CpmSystemProfileRegistry.ResolveVerifiedSource(source).Id);
        Assert.NotEqual(profile.ReferenceImageSha256, Sha(source.Serialize()));
    }

    [PersonalCpmReferenceFact]
    public void MinimalAndFullHaveSameFileBasedIdentityAndSerializedBootHash()
    {
        // When the external minimal fixture is absent, still exercise a minimal clone of the full reference.
        var full = Source();
        var minimal = File.Exists(MinimalReference) ? DskDocument.Open(MinimalReference) : Source();
        if (!File.Exists(MinimalReference))
        {
            foreach (var entry in minimal.FileSystem.ReadDirectory().Where(f => f.Key != "0:PCPM.SYS")) minimal.FileSystem.Delete(entry);
            minimal = Reopen(minimal);
        }
        else Assert.Equal("77E39B48D674F12E242B6B573E172600D2ECB3939D7C2D2A49C0F7EF2095E517", Sha(minimal.Serialize()));
        Assert.Single(minimal.FileSystem.ReadDirectory());
        Assert.NotEqual(Sha(full.Serialize()), Sha(minimal.Serialize()));
        Assert.Equal(CpmSystemProfileRegistry.ResolveVerifiedSource(full).FileBasedFingerprint, CpmSystemProfileRegistry.ResolveVerifiedSource(minimal).FileBasedFingerprint);
        var boot = minimal.Image.Tracks[1]!;
        Assert.Equal("A9AF2A7AE10898918DB860948BB3A17024A780BF40DFF6A0F1891B4E94CF0B0A", Sha(minimal.Serialize().AsSpan(boot.FileOffset, boot.BlockSize).ToArray()));
        var target = Blank();
        var report = DskBootSystemService.InstallWithReport(target, minimal);
        Assert.Single(target.FileSystem.ReadDirectory());
        Assert.Equal(CpmSystemStorageKind.BootTrackPlusSystemFile, report.Storage);
        Assert.Contains("PCPM.SYS", report.ToText());
        Assert.Null(report.TransferMode);
        Assert.Equal(0, DskAnalyzer.Analyze(target).Errors);
        Assert.StartsWith("Yes", DskBootInfo.Inspect(target).Bootable);
    }

    [PersonalCpmReferenceFact]
    public void OccupiedReferenceBlocksArePreservedAndSystemRelocated()
    {
        var target = Blank(); var source = Source(); byte[] sourceBefore = source.Serialize();
        byte[] content = Enumerable.Range(0, 16384).Select(i => (byte)(i * 17)).ToArray();
        target.FileSystem.Insert("KEEP.COM", content, user: 3);
        var fs = (CpmFileSystem)target.FileSystem;
        fs.UpdateAttributes(target.FileSystem.ReadDirectory().Single(), 3, true, true, true);
        target = Reopen(target);
        var beforeMap = DskAnalyzer.Analyze(target).Sectors.Where(s => s.Files == "KEEP.COM").Select(s => (s.Track, s.R)).ToArray();
        Assert.Equal(Enumerable.Range(1, 8), DskAnalyzer.Analyze(target).Sectors.SelectMany(s => s.Owners).Select(o => o.Block).Distinct().Order());
        var beforeReserved0 = target.Image.Tracks[0]!.Sectors.SelectMany(s => s.Data).ToArray();
        var profile = CpmSystemProfileRegistry.ResolveVerifiedSource(source);
        var report = CpmSystemBuilder.Apply(target, source, profile);
        var keep = target.FileSystem.ReadDirectory().Single(f => f.Name == "KEEP");
        Assert.Equal(content, target.FileSystem.Extract(keep));
        Assert.True(keep.ReadOnly && keep.System && keep.Archived); Assert.Equal(3, keep.User);
        Assert.Equal(beforeMap, DskAnalyzer.Analyze(target).Sectors.Where(s => s.Files == "KEEP.COM").Select(s => (s.Track, s.R)).ToArray());
        Assert.Equal(beforeReserved0, target.Image.Tracks[0]!.Sectors.SelectMany(s => s.Data));
        var blocks = DskAnalyzer.Analyze(target).Sectors.SelectMany(s => s.Owners).Where(o => o.FileKey == "0:PCPM.SYS").Select(o => o.Block).Distinct().ToArray();
        Assert.All(blocks, block => Assert.True(block > 8));
        Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(target));
        Assert.Equal(0, report.SystemFileDirectorySlot);
        Assert.NotNull(report.RelocatedFirstEntrySlot);
        Assert.Equal(blocks.Order(), report.SystemFileAllocationBlocks.Order());
        Assert.Contains("Previous slot 0 entry relocated: yes", report.ToText());
        Assert.Equal(profile.FileBasedFingerprint, CpmSystemProfileRegistry.ResolveVerifiedSource(target).FileBasedFingerprint);
        Assert.Equal(2, target.FileSystem.ReadDirectory().Count);
        Assert.Equal(sourceBefore, source.Serialize());
        Assert.Contains(report.ChangedRanges, r => r.PhysicalTrack != 1);
        Assert.Equal(new[] { 1 }, report.SystemPhysicalTracks);
        Assert.True(target.IsModified);
        Assert.All(DskAnalyzer.Analyze(target).Sectors.Where(s => s.Files == "PCPM.SYS"), s => Assert.True(s.Role.HasFlag(DskSectorRole.SystemFile)));
    }

    [PersonalCpmReferenceFact]
    public void ReplaceOldReadonlySystemButPreserveOtherUserSystemName()
    {
        var target = Blank();
        target.FileSystem.Insert("PCPM.SYS", new byte[128]);
        ((CpmFileSystem)target.FileSystem).UpdateAttributes(Sys(target), 0, true, false, true);
        target.FileSystem.Insert("PCPM.SYS", new byte[256], user: 2);
        target = Reopen(target);
        DskBootSystemService.Install(target, Source());
        Assert.Equal(2, target.FileSystem.ReadDirectory().Count);
        Assert.Equal(14848, Sys(target).Size); Assert.True(Sys(target).System);
        Assert.False(Sys(target).ReadOnly); Assert.False(Sys(target).Archived);
        Assert.Equal(new byte[256], target.FileSystem.Extract(target.FileSystem.ReadDirectory().Single(f => f.User == 2)));
    }

    [PersonalCpmReferenceFact]
    public void FullDiskAndFullDirectoryRollbackEvenWhenReplacingSystem()
    {
        foreach (bool directoryFull in new[] { false, true })
        {
            var target = Blank();
            if (directoryFull)
                for (int i = 0; i < 64; i++) target.FileSystem.Insert($"F{i:D3}.COM", new byte[128]);
            else
            {
                target.FileSystem.Insert("PCPM.SYS", new byte[128]);
                target.FileSystem.Insert("FULL.COM", new byte[154 * 2048]);
            }
            target = Reopen(target);
            Assert.Equal(0, DskAnalyzer.Analyze(target).Errors);
            byte[] before = target.Serialize();
            if (directoryFull)
                Assert.Contains("no free directory entry", Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(target, Source())).Message);
            else Assert.Throws<IOException>(() => DskBootSystemService.Install(target, Source()));
            Assert.Equal(before, target.Serialize()); Assert.False(target.IsModified);
        }
    }

    [PersonalCpmReferenceFact]
    public void PayloadOrIplMutationBreaksRegisteredMatchButRemainsUnverifiedCandidate()
    {
        foreach (bool mutateIpl in new[] { false, true })
        {
            var source = Source(); var profile = CpmSystemProfileRegistry.ResolveVerifiedSource(source);
            if (mutateIpl) source.Image.Tracks[1]!.Sectors[^1].Data[^1] ^= 1;
            else
            {
                var sector = DskAnalyzer.Analyze(source).Sectors.First(s => s.Files == "PCPM.SYS");
                source.Image.Tracks[sector.Track]!.Sectors[sector.PhysicalIndex].Data[0] ^= 1;
            }
            source = Reopen(source);
            Assert.Null(CpmSystemProfileRegistry.TryResolveVerifiedSource(source));
            Assert.StartsWith("Unverified", DskBootInfo.Inspect(source).Bootable);
            Assert.True(DskCapabilityService.CanInstallBootSystem(Blank(), source).IsCompatible);
            Assert.False(CpmSystemBuilder.Preflight(Blank(), source, profile).CanBuild);
            var target = Blank();
            var report = DskBootSystemService.InstallWithReport(target, source);
            Assert.Equal(CpmRuntimeVerification.None, report.Verification);
            Assert.Equal(source.FileSystem.Extract(Sys(source)), target.FileSystem.Extract(Sys(target)));
            Assert.StartsWith("Unverified", DskBootInfo.Inspect(target).Bootable);
        }
    }

    [PersonalCpmReferenceFact]
    public void FirstMultiExtentFileRelocatedByteForByteWithoutMovingData()
    {
        var target = Blank();
        target.FileSystem.Insert("LARGE.COM", Enumerable.Range(0, 40960).Select(i => (byte)(i * 7)).ToArray(), user: 4);
        var fs = (CpmFileSystem)target.FileSystem;
        fs.UpdateAttributes(target.FileSystem.ReadDirectory().Single(), 4, true, true, true);
        // Preserve even the otherwise unused S1 byte, not just the exposed attributes.
        byte[] first = fs.ReadRawDirectoryEntry(0); first[13] = 0x27; fs.WriteRawDirectoryEntry(0, first);
        target = Reopen(target); fs = (CpmFileSystem)target.FileSystem;
        byte[] otherExtent = fs.ReadRawDirectoryEntry(1);
        byte[] payload = fs.Extract(fs.ReadDirectory().Single());
        var locations = DskAnalyzer.Analyze(target).Sectors.Where(s => s.Files == "LARGE.COM").Select(s => (s.Track, s.R)).ToArray();
        var preflight = CpmSystemBuilder.Preflight(target, Source(), CpmSystemProfileRegistry.ResolveVerifiedSource(Source()));
        Assert.True(preflight.CanBuild); Assert.Contains("LARGE.COM", preflight.Report); Assert.Contains("entry #0", preflight.Report);
        var report = DskBootSystemService.InstallWithReport(target, Source());
        fs = (CpmFileSystem)target.FileSystem;
        Assert.Equal(2, report.RelocatedFirstEntrySlot);
        Assert.Equal(first, fs.ReadRawDirectoryEntry(2)); Assert.Equal(otherExtent, fs.ReadRawDirectoryEntry(1));
        Assert.Equal(payload, fs.Extract(fs.ReadDirectory().Single(f => f.Name == "LARGE")));
        Assert.Equal(locations, DskAnalyzer.Analyze(target).Sectors.Where(s => s.Files == "LARGE.COM").Select(s => (s.Track, s.R)).ToArray());
        Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(target)); Assert.Equal(0, DskAnalyzer.Analyze(target).Errors);
    }

    [PersonalCpmReferenceFact]
    public void ExistingSystemInSlotFiveReplacedWithoutDuplicatesEvenWithFullDirectory()
    {
        foreach (bool full in new[] { false, true })
        {
            var target = Blank();
            for (int index = 0; index < 5; index++) target.FileSystem.Insert($"F{index}.COM", new byte[128]);
            target.FileSystem.Insert("PCPM.SYS", new byte[128]);
            if (full)
                for (int index = 6; index < 64; index++) target.FileSystem.Insert($"F{index}.COM", new byte[128]);
            target = Reopen(target);
            var fs = (CpmFileSystem)target.FileSystem;
            Assert.Equal(5, Assert.Single(fs.DirectoryEntriesFor(Sys(target))).Index);
            byte[] previousFirst = fs.ReadRawDirectoryEntry(0);
            var report = DskBootSystemService.InstallWithReport(target, Source());
            fs = (CpmFileSystem)target.FileSystem;
            Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(target));
            Assert.Equal(0, Assert.Single(fs.DirectoryEntriesFor(Sys(target))).Index);
            Assert.Equal(5, report.RelocatedFirstEntrySlot);
            Assert.Equal(previousFirst, fs.ReadRawDirectoryEntry(5));
            Assert.Equal(full ? 64 : 6, fs.ReadDirectory().Count);
            Assert.Equal(0, DskAnalyzer.Analyze(target).Errors);
        }
    }

    [PersonalCpmReferenceFact]
    public void LaterSystemIsNotBootableButCanBeReinstalledIntoFirstEntry()
    {
        var source = Source(); var fs = (CpmFileSystem)source.FileSystem;
        fs.MoveDirectoryEntry(0, fs.FindFreeDirectorySlot(1)); source = Reopen(source);
        Assert.StartsWith("No", DskBootInfo.Inspect(source).Bootable);
        Assert.Contains("not the first", DskBootInfo.Inspect(source).Bootable);
        Assert.False(DskBootInfo.Inspect(source).HasSystemBytes);
        Assert.Null(CpmSystemProfileRegistry.TryResolveVerifiedSource(source));
        Assert.Contains(DskAnalyzer.Analyze(source).Issues, i => i.Code == "PCPM_SYS_NOT_FIRST_DIRECTORY_ENTRY");
        var target = Blank(); Assert.True(DskCapabilityService.CanInstallBootSystem(target, source).IsCompatible);
        DskBootSystemService.Install(target, source);
        Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(target));
        Assert.StartsWith("Yes", DskBootInfo.Inspect(target).Bootable);
        Assert.Equal(PersonalCpmSystemInstaller.ProfileId, CpmSystemProfileRegistry.ResolveVerifiedSource(target).Id);
    }

    [PersonalCpmReferenceFact]
    public void DuplicateOrMultipleSystemExtentsRejectedWithoutHeuristicRepair()
    {
        foreach (bool duplicate in new[] { true, false })
        {
            var target = Blank();
            target.FileSystem.Insert("PCPM.SYS", new byte[duplicate ? 128 : 40960]);
            var fs = (CpmFileSystem)target.FileSystem;
            if (duplicate) fs.WriteRawDirectoryEntry(1, fs.ReadRawDirectoryEntry(0));
            target = Reopen(target);
            byte[] before = target.Serialize();
            Assert.Contains(DskAnalyzer.Analyze(target).Issues, i => i.Code == "PCPM_SYS_INVALID_EXTENTS" && i.Severity == DskIssueSeverity.Unsafe);
            Assert.False(DskCapabilityService.CanInstallBootSystem(target, Source()).IsCompatible);
            Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(target, Source()));
            Assert.Equal(before, target.Serialize()); Assert.False(target.IsModified);
        }
        var source = Source(); var sourceFs = (CpmFileSystem)source.FileSystem;
        sourceFs.WriteRawDirectoryEntry(sourceFs.FindFreeDirectorySlot(), sourceFs.ReadRawDirectoryEntry(0));
        source = Reopen(source);
        Assert.StartsWith("No", DskBootInfo.Inspect(source).Bootable);
        Assert.Null(CpmSystemProfileRegistry.TryResolveVerifiedSource(source));
        Assert.False(DskCapabilityService.CanInstallBootSystem(Blank(), source).IsCompatible);
    }

    [Fact]
    public void DirectoryApiChecksBoundsSizesOccupiedDestinationAndPreservesExtent()
    {
        var doc = Blank(); var fs = (CpmFileSystem)doc.FileSystem;
        doc.FileSystem.Insert("FIRST.COM", new byte[128]); doc.FileSystem.Insert("SECOND.COM", new byte[128]);
        byte[] first = fs.ReadRawDirectoryEntry(0);
        byte[] before = doc.Image.Serialize();
        Assert.Throws<ArgumentOutOfRangeException>(() => fs.ReadRawDirectoryEntry(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => fs.ReadRawDirectoryEntry(64));
        Assert.Throws<ArgumentException>(() => fs.WriteRawDirectoryEntry(0, new byte[31]));
        Assert.Throws<ArgumentException>(() => fs.WriteRawDirectoryEntry(0, new byte[33]));
        Assert.Throws<ArgumentOutOfRangeException>(() => fs.WriteRawDirectoryEntry(64, first));
        Assert.Throws<ArgumentOutOfRangeException>(() => fs.FindFreeDirectorySlot(-1));
        Assert.Throws<InvalidDataException>(() => fs.MoveDirectoryEntry(0, 1));
        Assert.Equal(before, doc.Image.Serialize());
        Assert.Equal(2, fs.FindFreeDirectorySlot()); Assert.Equal(-1, fs.FindFreeDirectorySlot(64));
        fs.MoveDirectoryEntry(0, 2);
        Assert.Equal(first, fs.ReadRawDirectoryEntry(2)); Assert.Equal(0xE5, fs.ReadRawDirectoryEntry(0)[0]);
        Assert.Throws<InvalidDataException>(() => fs.MoveDirectoryEntry(0, 3));
        Assert.Equal(new byte[128], fs.Extract(fs.ReadDirectory().Single(f => f.Name == "FIRST")));
        Assert.Equal(0, DskAnalyzer.Analyze(doc).Errors);
    }

    [PersonalCpmReferenceFact]
    public void NativeSystemCannotUseHiddenTracksProfileAndWrongDpbRejected()
    {
        var source = Source(); var target = Blank();
        var hidden = CpmSystemProfileRegistry.ResolveVerifiedSource(source) with { Storage = CpmSystemStorageKind.HiddenSystemTracks };
        byte[] before = target.Serialize();
        Assert.Throws<InvalidDataException>(() => CpmSystemFingerprint.From(source));
        Assert.False(CpmSystemBuilder.Preflight(target, source, hidden).CanBuild);
        Assert.Throws<InvalidDataException>(() => CpmSystemBuilder.Apply(target, source, hidden));
        Assert.Equal(before, target.Serialize());
        var wrongDpb = CpmDpb.PersonalCpm80 with { Cks = 17 };
        Assert.True(CpmFileSystem.TryOpen(target.Image, wrongDpb, out var wrongFs));
        var wrong = new DskDocument(target.Image, before, null, wrongFs!);
        Assert.False(DskCapabilityService.CanInstallBootSystem(wrong, source).IsCompatible);
        Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(wrong, source));
        Assert.Equal(before, wrong.Serialize());
    }

    [PersonalCpmReferenceFact]
    public void SourceErrorsFdcAndChangedNativeOrderNeverModifyTarget()
    {
        foreach (string kind in new[] { "fdc", "order", "directory" })
        {
            var source = Source(); var target = Blank();
            switch (kind)
            {
                case "fdc": source.Image.Tracks[1]!.Sectors[0].FdcStatus2 = 0x20; break;
                case "order": source.Image.Tracks[60]!.Sectors.Reverse(); break;
                case "directory": source.Image.Tracks[2]!.Sectors.First(s => s.SectorId == 1).Data[15] = 255; break;
            }
            source = DskDocument.Open(source.Image.Serialize());
            byte[] before = target.Serialize();
            Assert.Null(CpmSystemProfileRegistry.TryResolveVerifiedSource(source));
            Assert.False(DskCapabilityService.CanInstallBootSystem(target, source).IsCompatible);
            Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(target, source));
            Assert.Equal(before, target.Serialize()); Assert.False(target.IsModified);
        }
    }

    [PersonalCpmReferenceFact]
    public void MissingSystemHeaderOnlyBadGeometryAndOtherLayoutsRejected()
    {
        var source = Source(); source.FileSystem.Delete(Sys(source)); source = Reopen(source);
        Assert.StartsWith("No", DskBootInfo.Inspect(source).Bootable);
        Assert.Contains("missing", DskBootInfo.Inspect(source).Bootable);
        Assert.Contains(DskAnalyzer.Analyze(source).Issues, i => i.Code == "PCPM_SYSTEM_FILE" && i.Description.Contains("No"));
        Assert.False(DskCapabilityService.CanInstallBootSystem(Blank(), source).IsCompatible);
        Assert.False(DskCapabilityService.CanInstallBootSystem(Blank(), Blank()).IsCompatible);
        foreach (var target in new[] { DskDocumentFactory.CreateCpm(false), DskDocumentFactory.CreateCpm(true), DskDocumentFactory.CreateCpm(false, 40, 2), DskDocumentFactory.CreatePersonalCpm80(true) })
        {
            byte[] before = target.Serialize();
            Assert.False(DskCapabilityService.CanInstallBootSystem(target, Source()).IsCompatible);
            Assert.Throws<InvalidDataException>(() => DskBootSystemService.Install(target, Source()));
            Assert.Equal(before, target.Serialize());
        }
        var malformed = Source(); malformed.Image.Tracks[1]!.Sectors.Reverse(); malformed = Reopen(malformed);
        Assert.Null(CpmSystemProfileRegistry.TryResolveVerifiedSource(malformed));
        Assert.False(DskCapabilityService.CanInstallBootSystem(Blank(), malformed).IsCompatible);
        var wrongDpb = new DskDocument(Source().Image, Source().Serialize(), null, new RawDskFileSystem(Source().Image));
        Assert.False(DskCapabilityService.CanInstallBootSystem(Blank(), wrongDpb).IsCompatible);
    }
}
