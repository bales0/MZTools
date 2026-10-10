using System.Buffers.Binary;
using System.Text.Json;

namespace MZTools.Tests;

public sealed class PhaseEWorkflowTests : IDisposable
{
    private readonly string folder = TapeTestData.CreateTempDirectory();
    public void Dispose() => Directory.Delete(folder, true);
    private static DskDocument Disk(string type) => type switch
    {
        "fsmz" => DskDocumentFactory.CreateFsmz(),
        "mrs" => DskDocumentFactory.CreateMrs(),
        _ => DskDocumentFactory.CreateCpm(false)
    };

    [Theory]
    [InlineData("fsmz", "fsmz")] [InlineData("fsmz", "cpm")] [InlineData("fsmz", "mrs")]
    [InlineData("cpm", "fsmz")] [InlineData("cpm", "cpm")] [InlineData("cpm", "mrs")]
    [InlineData("mrs", "fsmz")] [InlineData("mrs", "cpm")] [InlineData("mrs", "mrs")]
    public void CopyPreservesPayloadAndExistingFilesWithoutChangingSource(string from, string to)
    {
        var source = Disk(from); var target = Disk(to);
        source.FileSystem.Insert("TEST.BIN", Enumerable.Range(0, 500).Select(i => (byte)i).ToArray(), 1, 0x4000, 0x4000, from == "cpm" ? 3 : 0);
        target.FileSystem.Insert("KEEP.BIN", new byte[128]); source.MarkModified(); target.MarkModified();
        byte[] beforeSource = source.Serialize(), beforeTarget = target.Serialize();
        var entry = source.FileSystem.ReadDirectory().Single(); byte[] payload = source.FileSystem.Extract(entry);
        var result = CrossDiskTransferService.Preview(source, target, [new(entry.Key)], DiskCollisionPolicy.Cancel);
        Assert.Equal(beforeSource, source.Serialize()); Assert.Equal(beforeTarget, target.Serialize());
        Assert.Contains("directory", result.Operation.Report, StringComparison.OrdinalIgnoreCase);
        if (from == "fsmz" && to == "cpm") Assert.Contains("loses LOAD/EXEC", result.Operation.Report);
        result.Operation.Apply(target);
        var reopened = target.Reopen(target.Serialize());
        var copied = reopened.FileSystem.ReadDirectory().Single(e => e.Name == "TEST" || e.Name == "TEST.BIN");
        Assert.Equal(payload, reopened.FileSystem.Extract(copied).Take(payload.Length));
        Assert.Contains(reopened.FileSystem.ReadDirectory(), e => e.Name == "KEEP" || e.Name == "KEEP.BIN");
        Assert.Equal(beforeSource, source.Serialize()); Assert.Equal(0, DskAnalyzer.Analyze(reopened).Errors);
        if (from == "cpm" && to == "cpm") Assert.Equal(3, copied.User);
        if (from != "cpm" && to != "cpm") Assert.Equal((ushort)0x4000, copied.LoadAddress);
    }

    [Theory]
    [InlineData((int)DiskCollisionPolicy.Skip, 1, 2)]
    [InlineData((int)DiskCollisionPolicy.Replace, 1, 1)]
    [InlineData((int)DiskCollisionPolicy.Rename, 2, 2)]
    public void CollisionPolicyIsExplicit(int policy, int count, byte originalByte)
    {
        var source = Disk("fsmz"); var target = Disk("fsmz");
        source.FileSystem.Insert("TEST", [1]); target.FileSystem.Insert("TEST", [2]); source.MarkModified(); target.MarkModified();
        var preview = CrossDiskTransferService.Preview(source, target, [new(source.FileSystem.ReadDirectory()[0].Key)], (DiskCollisionPolicy)policy);
        preview.Operation.Apply(target);
        Assert.Equal(count, target.FileSystem.ReadDirectory().Count);
        Assert.Equal(originalByte, target.FileSystem.Extract(target.FileSystem.ReadDirectory().Single(e => e.Name == "TEST"))[0]);
    }

    [Fact]
    public void CollisionCancelAndLaterInsertionFailureNeverModifyTarget()
    {
        var source = Disk("fsmz"); var target = Disk("cpm");
        source.FileSystem.Insert("GOOD.BIN", [1]); source.FileSystem.Insert("BAD NAME", [2]);
        source.MarkModified(); byte[] before = target.Serialize();
        Assert.Throws<InvalidDataException>(() => CrossDiskTransferService.Preview(source, target,
            source.FileSystem.ReadDirectory().OrderBy(e => e.Name == "GOOD.BIN" ? 0 : 1).Select(e => new DiskCopyRequest(e.Key)).ToArray(), DiskCollisionPolicy.Cancel));
        Assert.Equal(before, target.Serialize());
        target.FileSystem.Insert("GOOD.BIN", [3]); target.MarkModified(); before = target.Serialize();
        Assert.Throws<InvalidDataException>(() => CrossDiskTransferService.Preview(source, target,
            [new(source.FileSystem.ReadDirectory().Single(e => e.Name == "GOOD.BIN").Key)], DiskCollisionPolicy.Cancel));
        Assert.Equal(before, target.Serialize());
    }

    [Fact]
    public void CopyPreservesCpmAttributesAndRejectsProtectedReplacement()
    {
        var source = Disk("cpm"); var target = Disk("cpm");
        source.FileSystem.Insert("A.COM", [0xC9], user: 4); source.MarkModified();
        var entry = source.FileSystem.ReadDirectory().Single(); ((CpmFileSystem)source.FileSystem).SetAttributes(entry, true, true, true); source.MarkModified();
        CrossDiskTransferService.Preview(source, target, [new(entry.Key)], DiskCollisionPolicy.Cancel).Operation.Apply(target);
        var copied = target.FileSystem.ReadDirectory().Single(); Assert.True(copied.ReadOnly && copied.System && copied.Archived); Assert.Equal(4, copied.User);
        byte[] before = target.Serialize();
        Assert.Throws<InvalidDataException>(() => CrossDiskTransferService.Preview(source, target, [new(entry.Key)], DiskCollisionPolicy.Replace)); Assert.Equal(before, target.Serialize());
    }

    [Fact]
    public void CopyPreviewRejectsChangedTargetAndExhaustedDirectory()
    {
        var source = Disk("fsmz"); source.FileSystem.Insert("NEW", [1]); source.MarkModified(); var target = DskDocumentFactory.CreateFsmz(false);
        var preview = CrossDiskTransferService.Preview(source, target, [new(source.FileSystem.ReadDirectory()[0].Key)], DiskCollisionPolicy.Cancel);
        target.FileSystem.Insert("CHANGED", [2]); target.MarkModified(); byte[] changed = target.Serialize();
        Assert.Throws<InvalidOperationException>(() => preview.Operation.Apply(target)); Assert.Equal(changed, target.Serialize());
        for (int i = 1; i < 63; i++) target.FileSystem.Insert("F" + i, [1]); target.MarkModified(); changed = target.Serialize();
        Assert.ThrowsAny<IOException>(() => CrossDiskTransferService.Preview(source, target, [new(source.FileSystem.ReadDirectory()[0].Key)], DiskCollisionPolicy.Cancel)); Assert.Equal(changed, target.Serialize());
    }

    [Fact]
    public void LayoutProfilesRoundTripMapsAndProtectBuiltIns()
    {
        var dpb = CpmDpb.PersonalCpm80;
        var profile = new CpmLayoutProfile(Guid.NewGuid().ToString("N"), "Custom", "Description", dpb);
        UserProfileService.Save(profile, folder);
        var loaded = UserProfileService.Layouts(folder).Single(p => !p.BuiltIn);
        Assert.Equal(dpb.PhysicalTrackMap, loaded.Dpb.PhysicalTrackMap);
        Assert.Equal(CpmDpbSignature.From(dpb), CpmDpbSignature.From(loaded.Dpb));
        UserProfileService.Save(loaded with { Name = "Renamed" }, folder);
        var duplicate = loaded with { Id = Guid.NewGuid().ToString("N"), Name = "Duplicate" }; UserProfileService.Save(duplicate, folder);
        Assert.Equal(2, UserProfileService.Layouts(folder).Count(p => !p.BuiltIn));
        var builtIn = UserProfileService.Layouts(folder).First(p => p.BuiltIn);
        Assert.Throws<InvalidDataException>(() => UserProfileService.Save(builtIn, folder));
        Assert.Throws<InvalidDataException>(() => UserProfileService.DeleteLayout(builtIn, folder));
        UserProfileService.DeleteLayout(duplicate, folder); Assert.Single(UserProfileService.Layouts(folder), p => !p.BuiltIn);
        var sector = profile with { Id = Guid.NewGuid().ToString("N"), Dpb = CpmDpb.Sds400 }; UserProfileService.Save(sector, folder);
        Assert.Equal(CpmDpb.Sds400.PhysicalSectorMap, UserProfileService.Layouts(folder).Single(p => p.Id == sector.Id).Dpb.PhysicalSectorMap);
    }

    [Fact]
    public void LayoutAttachIsReadOnlyAndEnforcesGeometry()
    {
        var document = Disk("cpm"); byte[] bytes = document.Serialize();
        var profile = new CpmLayoutProfile(Guid.NewGuid().ToString("N"), "Custom", "", ((CpmFileSystem)document.FileSystem).Dpb, DskGeometrySignature.From(document.Image));
        var preview = UserProfileService.Preview(document, profile); Assert.True(preview.CanApply, preview.Report);
        CpmLayoutService.Apply(document, preview); Assert.Equal(bytes, document.Serialize()); Assert.False(document.IsModified);
        Assert.Throws<InvalidDataException>(() => UserProfileService.Preview(DskDocumentFactory.CreateCpm(true), profile));
        Assert.Throws<InvalidDataException>(() => UserProfileService.Save(profile with { Id = "../escape" }, folder));
    }

    [Fact]
    public void ProfileWriteFailureLeavesPreviousProfileAndCleansTemporary()
    {
        var profile = new CpmLayoutProfile(Guid.NewGuid().ToString("N"), "Original", "", CpmDpb.Dd); UserProfileService.Save(profile, folder);
        string badId = Guid.NewGuid().ToString("N"); Directory.CreateDirectory(Path.Combine(folder, "layout", badId + ".json"));
        Assert.IsType<UnauthorizedAccessException>(Record.Exception(() => UserProfileService.Save(profile with { Id = badId }, folder)));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal("Original", UserProfileService.Layouts(folder).Single(p => p.Id == profile.Id).Name);
    }

    [Fact]
    public void VerifyAndNormalizePreservePayloadAndOpaqueBytes()
    {
        var source = Disk("cpm"); source.FileSystem.Insert("TEST.COM", [0xC9]); source.MarkModified();
        byte[] bytes = source.Serialize(); bytes[0x2F] = 0x20; bytes[0x33] = 0xA5;
        bytes = bytes.Concat(new byte[] { 7, 8, 9 }).ToArray(); var document = DskDocument.Open(bytes);
        var verified = ImageVerificationService.Verify(bytes); Assert.Equal(bytes, document.Serialize()); Assert.Contains("SHA-256", verified.Report);
        var preview = ContainerNormalizeService.Preview(document); Assert.Equal(bytes, document.Serialize());
        byte[] after = preview.Result; Assert.Equal(0, after[0x2F]); Assert.Equal(0xA5, after[0x33]); Assert.Equal(new byte[] { 7, 8, 9 }, after[^3..]);
        Assert.All(DskCompareService.ByteDifferences(bytes, after), d => Assert.InRange(d.Offset, 0x22, 0x2F));
        preview.Apply(document); Assert.Equal(after, document.Serialize());
        Assert.Equal(after, ContainerNormalizeService.Preview(document).Result);
        Assert.Equal(DskIssueSeverity.Error, ImageVerificationService.Verify([1, 2, 3]).Severity);
    }

    [Theory]
    [InlineData(0x100, 0x100, 16, false, false, true)]
    [InlineData(0, 0, 16, true, false, true)]
    [InlineData(0xFFF0, 0xFFF0, 32, false, true, false)]
    [InlineData(0x1200, 0x2000, 16, false, false, false)]
    public void AnalyzerNeverMistakesAddressesForCompatibility(int load, int exec, int size, bool low, bool tpa, bool entry)
    {
        var bytes = TapeTestData.CreateMzf(new byte[size]);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)load); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), (ushort)exec);
        var analysis = MzfCpmCompatibilityAnalyzer.Analyze(TapeTestData.ReadRecord(bytes));
        Assert.Equal(low, analysis.LowMemoryOverlap); Assert.Equal(tpa, analysis.TpaOverlap); Assert.Equal(entry, analysis.EntryWithinBody);
        Assert.False(analysis.CanConvert); Assert.Equal(CompatibilityEvidence.Unknown, analysis.Evidence);
    }

    [Fact]
    public void AnalyzerFlagsKnownMultipartAndInstructionPatternsAsHeuristics()
    {
        var analysis = MzfCpmCompatibilityAnalyzer.Analyze(TapeTestData.ReadRecord(TapeTestData.CreateMzf([0xCD, 0x05, 0, 0xD3, 0xE0])), true);
        Assert.True(analysis.PossibleMultipart); Assert.Contains(analysis.Warnings, w => w.Contains("Heuristic")); Assert.False(analysis.CanConvert);
        var invalid = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1])); var header = invalid.Header; header.MzfSize = 2; invalid.Header = header;
        Assert.Throws<InvalidDataException>(() => MzfCpmCompatibilityAnalyzer.Analyze(invalid));
    }

    [Fact]
    public void BatchAnalysisExportsCommonReportWithoutWritingCom()
    {
        string path = Path.Combine(folder, "TEST.M12"); File.WriteAllBytes(path, TapeTestData.CreateMzf([0xC9]));
        var preview = BatchProcessService.Preview([path], new(BatchOperation.AnalyzeMzfCompatibility, folder));
        var row = Assert.Single(preview.Rows); Assert.Equal("Will analyze", row.Result); Assert.Empty(row.Output); Assert.False(row.ImageBytes?.Length > 0);
        using var json = JsonDocument.Parse(BatchProcessService.Report(preview, "json"));
        var item = json.RootElement[0]; Assert.Equal("Unknown", item.GetProperty("Evidence").GetString()); Assert.True(item.TryGetProperty("Validation", out _)); Assert.True(item.TryGetProperty("Severity", out _));
        Assert.Contains("conversionMode,profile,evidence", BatchProcessService.Report(preview, "csv"));
        Assert.Contains("Unsupported", BatchProcessService.Report(preview, "txt"));
        BatchProcessService.Execute(preview); Assert.Equal("Analyzed", row.Result); Assert.Empty(Directory.GetFiles(folder, "*.com"));
    }

    [Fact]
    public void CompareNamesPayloadOnlyAndTracksGapChanges()
    {
        var source = Disk("cpm"); var other = source.Clone(); other.Image.Tracks[0]!.Sectors[0].Data[1] ^= 1; other.MarkModified();
        var diff = DskCompareService.Compare(source, other).Items.Single(i => i.Level == DskDiffLevel.PhysicalSectors && i.State == DskDiffState.Changed);
        Assert.Equal("Payload only", diff.DifferenceKind);
        other = source.Clone(); other.Image.Tracks[0]!.Gap ^= 1; other.MarkModified();
        Assert.Contains(DskCompareService.Compare(source, other).Items, i => i.Level == DskDiffLevel.PhysicalSectors && i.Name.Contains("physical geometry") && i.State == DskDiffState.Changed);
    }

    [Fact]
    public void CrossFilesystemCollisionRenamePreservesTheExtension()
    {
        var source = Disk("fsmz"); var target = Disk("cpm");
        source.FileSystem.Insert("TEST.BIN", [1]); target.FileSystem.Insert("TEST.BIN", [2]); source.MarkModified(); target.MarkModified();
        CrossDiskTransferService.Preview(source, target, [new(source.FileSystem.ReadDirectory().Single().Key)], DiskCollisionPolicy.Rename).Operation.Apply(target);
        Assert.Contains(target.FileSystem.ReadDirectory(), e => e.Name == "TEST~1" && e.Extension == "BIN");
        Assert.Throws<ArgumentException>(() => CrossDiskTransferService.Preview(source, target, [new(source.FileSystem.ReadDirectory().Single().Key)], (DiskCollisionPolicy)99));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HfeVerificationAndComparisonDoNotClaimCaptureIdentityFromDecodedData(bool v3)
    {
        var document = DskDocumentFactory.CreateFsmz(tracks: 4); document.FileSystem.Insert("TEST", [1, 2, 3]); document.MarkModified();
        byte[] hfe = MediaConversionService.DskToHfe(document, v3).Output, original = (byte[])hfe.Clone();
        var report = ImageVerificationService.Verify(hfe);
        Assert.NotEqual(DskIssueSeverity.Error, report.Severity); Assert.Contains("header/LUT", report.Report); Assert.Equal(original, hfe);
        string cross = DskCompareService.CompareMedia(document.Serialize(), hfe);
        Assert.Contains("Physical capture comparison unavailable", cross); Assert.Contains("does not establish physical identity", cross);
        byte[] changedHeader = (byte[])hfe.Clone(); changedHeader[100] ^= 1;
        string comparison = DskCompareService.CompareMedia(hfe, changedHeader);
        Assert.Contains("Container bytes: differ", comparison); Assert.Contains("0 differing tracks", comparison);
        byte[] truncated = hfe[..600]; Assert.Equal(DskIssueSeverity.Error, ImageVerificationService.Verify(truncated).Severity);
    }

    [PersonalCpmReferenceFact]
    public void BootProfilePreservesNativeInstallerConstraintsAndSupportsBatch()
    {
        string sourcePath = Path.Combine(folder, "source.dsk"); File.Copy(PersonalCpmSystemTests.FullReference, sourcePath);
        var source = DskDocument.Open(sourcePath); var profile = UserProfileService.CaptureBoot(source, "Native", "Test");
        Assert.Equal(CpmSystemStorageKind.BootTrackPlusSystemFile, profile.Storage); Assert.Equal(0, profile.RequiredDirectorySlot); Assert.Contains("PCPM.SYS", profile.RequiredSystemFile);
        UserProfileService.Save(profile, folder); var saved = Assert.Single(UserProfileService.BootProfiles(folder));
        Assert.Equal(source.Serialize(), UserProfileService.ResolveBootSource(saved).Serialize());
        string targetPath = Path.Combine(folder, "target.dsk"); DskDocumentFactory.CreatePersonalCpm80(false).Save(targetPath);
        string profilePath = Path.Combine(folder, "boot", profile.Id + ".json");
        var batch = BatchProcessService.Preview([targetPath], new(BatchOperation.InstallBootSystem, Path.Combine(folder, "out"), AuxiliaryPath: profilePath));
        Assert.Equal("Will modify", Assert.Single(batch.Rows).Result); BatchProcessService.Execute(batch);
        Assert.Equal("Completed", batch.Rows[0].Result);
        var installed = DskDocument.Open(batch.Rows[0].Output); Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(installed));
        byte[] beforeTarget = File.ReadAllBytes(targetPath); byte[] altered = File.ReadAllBytes(sourcePath); altered[^1] ^= 1; File.WriteAllBytes(sourcePath, altered);
        Assert.Throws<InvalidDataException>(() => UserProfileService.ResolveBootSource(saved)); Assert.Equal(beforeTarget, File.ReadAllBytes(targetPath));
    }

    [Fact]
    public void SaveReportsActualOutputHashAndFailedCommitPreservesDocumentState()
    {
        var document = Disk("fsmz"); document.FileSystem.Insert("TEST", [1]); document.MarkModified(); byte[] before = document.Serialize();
        string blocked = Path.Combine(folder, "blocked.dsk"); Directory.CreateDirectory(blocked);
        Assert.IsType<UnauthorizedAccessException>(Record.Exception(() => document.Save(blocked)));
        Assert.True(document.IsModified); Assert.Null(document.FilePath); Assert.Null(document.LastSavedSha256); Assert.Equal(before, document.Serialize());
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        string output = Path.Combine(folder, "saved.dsk"); document.Save(output);
        Assert.Equal(before, File.ReadAllBytes(output)); Assert.Equal(ImageVerificationService.Hash(before), document.LastSavedSha256); Assert.False(document.IsModified);
    }

    [MzxResearchCorpusFact]
    public void OptionalMZXCorpusMatchesDocumentedFingerprintAndRunnerPath()
    {
        var document = DskDocument.Open(MzxResearchCorpusFactAttribute.Path);
        var utility = document.FileSystem.ReadDirectory().Single(e => e.Name == "MZX" && e.Extension == "COM" && e.User == 15);
        byte[] body = document.FileSystem.Extract(utility);
        Assert.Equal("590E9E69248CD4046AC0D997A47546291984FFA381293ED1731CC9CCBA271FC7", ImageVerificationService.Hash(body));
        // Reachable R command: relocate helper, change interrupts/mode and I/O.
        Assert.Equal(new byte[] { 0x31, 0xF0, 0x10, 0x21, 0x33, 0x02, 0x11, 0x00, 0x10, 0x01, 0x82, 0x00, 0xED, 0xB0 }, body.AsSpan(0x200 - 0x100, 14).ToArray());
        Assert.Equal(new byte[] { 0xD3, 0xFC, 0xD3, 0xFD, 0xF3, 0xED, 0x56 }, body.AsSpan(0x210 - 0x100, 7).ToArray());
    }
}

public sealed class MzxResearchCorpusFactAttribute : FactAttribute
{
    internal static string Path => System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../MZTool.Tests/DSK/CPMv41 Programy Demo 1 MZF.dsk"));
    public MzxResearchCorpusFactAttribute() { if (!File.Exists(Path)) Skip = "Optional historical MZX corpus not installed; acquisition and SHA-256 are documented in docs/MZF_CPM_INTEROP_RESEARCH.md."; }
}
