using System.Text.Json;

namespace MZTools.Tests;

public sealed class BatchProcessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MZToolsBatchTests-" + Guid.NewGuid().ToString("N"));
    public BatchProcessTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private string Disk(string name, DskDocument? document = null)
    {
        string path = Path.Combine(root, name); File.WriteAllBytes(path, (document ?? DskDocumentFactory.CreateFsmz()).Serialize()); return path;
    }
    private BatchOptions Options(BatchOperation operation = BatchOperation.Defragment, bool strict = false) => new(operation, Path.Combine(root, "out"), StrictPreflight: strict);
    private static DskDocument Fragmented()
    {
        var document = DskDocumentFactory.CreateFsmz(); document.FileSystem.Insert("FIRST", new byte[512]); document.FileSystem.Insert("SECOND", [1, 2, 3]);
        document.FileSystem.Delete(document.FileSystem.ReadDirectory().Single(e => e.Name == "FIRST")); document.MarkModified(); return document;
    }

    [Fact]
    public void DryRunWritesNothingAndPerFileContinuesPastFailure()
    {
        string good = Disk("good.dsk", Fragmented()); string bad = Path.Combine(root, "bad.dsk"); File.WriteAllBytes(bad, [1, 2, 3]); var before = File.ReadAllBytes(good);
        var preview = BatchProcessService.Preview([bad, good], Options());
        Assert.False(Directory.Exists(Path.Combine(root, "out"))); Assert.Equal(before, File.ReadAllBytes(good));
        BatchProcessService.Execute(preview);
        Assert.Equal("Completed", preview.Rows.Single(r => r.Input == good).Result);
        Assert.Equal(before, File.ReadAllBytes(good)); Assert.True(File.Exists(preview.Rows.Single(r => r.Input == good).Output));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path.Combine(root, "out")), p => p.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void StrictPreflightBlocksAllOutputs()
    {
        string good = Disk("good.dsk", Fragmented()); string bad = Path.Combine(root, "bad.dsk"); File.WriteAllBytes(bad, [0]);
        var preview = BatchProcessService.Preview([good, bad], Options(strict: true));
        Assert.Throws<InvalidOperationException>(() => BatchProcessService.Execute(preview)); Assert.False(Directory.Exists(Path.Combine(root, "out")));
    }

    [Fact]
    public void ChangedInputAndExistingOutputLeaveNoPartialTarget()
    {
        string good = Disk("good.dsk", Fragmented()); var preview = BatchProcessService.Preview([good], Options());
        File.WriteAllBytes(good, [0]); BatchProcessService.Execute(preview);
        Assert.Equal("Error", preview.Rows.Single().Result); Assert.False(File.Exists(preview.Rows.Single().Output));
        good = Disk("good.dsk", Fragmented()); preview = BatchProcessService.Preview([good], Options());
        Directory.CreateDirectory(Path.GetDirectoryName(preview.Rows.Single().Output)!); File.WriteAllBytes(preview.Rows.Single().Output, [99]);
        BatchProcessService.Execute(preview); Assert.Equal("Error", preview.Rows.Single().Result);
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(preview.Rows.Single().Output));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "out"), "*.tmp"));
    }

    [Fact]
    public void CatalogExportsEveryRequiredFieldAndAnalyzeDoesNotWriteImages()
    {
        string path = Disk("catalog.dsk"); var preview = BatchProcessService.Preview([path], Options(BatchOperation.Analyze));
        BatchProcessService.Execute(preview); Assert.Equal("Analyzed", preview.Rows.Single().Result); Assert.False(Directory.Exists(Path.Combine(root, "out")));
        string json = BatchProcessService.Report(preview, "json"); using var doc = JsonDocument.Parse(json); Assert.True(doc.RootElement[0].GetProperty("Catalog").GetProperty("Sha256").GetString()!.Length == 64);
        Assert.DoesNotContain("ImageBytes", json); Assert.Contains("analyzerErrors", BatchProcessService.Report(preview, "csv"));
        string txt = BatchProcessService.Report(preview, "txt");
        Assert.Contains("Input SHA-256:", txt); Assert.Contains("layout / DPB:", txt);
        Assert.Contains("bootable state:", txt); Assert.Contains("free:", txt); Assert.Contains("unsafe:", txt);
    }

    [Fact]
    public void ExtractAllPreservesPayloadsAndSeparatesCpmUsers()
    {
        var disk = DskDocumentFactory.CreateCpm(false); disk.FileSystem.Insert("TEST.BIN", [1, 2, 3], user: 1); disk.FileSystem.Insert("TEST.BIN", [4, 5, 6], user: 2); disk.MarkModified();
        string path = Disk("cpm.dsk", disk); var before = File.ReadAllBytes(path); var preview = BatchProcessService.Preview([path], Options(BatchOperation.ExtractAll));
        Assert.Equal("Will modify", preview.Rows.Single().Result); BatchProcessService.Execute(preview);
        Assert.Equal("Completed", preview.Rows.Single().Result); string output = preview.Rows.Single().Output;
        Assert.Equal(disk.FileSystem.Extract(disk.FileSystem.ReadDirectory().Single(e => e.User == 1)), File.ReadAllBytes(Path.Combine(output, "user1", "TEST.BIN")));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ApplyPatchUsesSameSourceFingerprintAndValidation()
    {
        var source = DskDocumentFactory.CreateFsmz(); var target = source.Clone(); target.Image.Tracks[4]!.Sectors[0].Data[0] ^= 1; target.MarkModified();
        string input = Disk("patch.dsk", source); string patchPath = Path.Combine(root, "patch.json"); File.WriteAllText(patchPath, JsonSerializer.Serialize(DskPatchService.Export(source.Serialize(), target.Serialize())));
        var preview = BatchProcessService.Preview([input], Options(BatchOperation.ApplyPatch) with { AuxiliaryPath = patchPath });
        Assert.Equal("Will modify", preview.Rows.Single().Result); BatchProcessService.Execute(preview); Assert.Equal(target.Serialize(), File.ReadAllBytes(preview.Rows.Single().Output));
    }

    [Fact]
    public void BootInstallRejectsUnregisteredAndIncompatibleSource()
    {
        string input = Disk("target.dsk", DskDocumentFactory.CreateCpm(false)); string system = Disk("system.dsk", DskDocumentFactory.CreateCpm(true));
        var preview = BatchProcessService.Preview([input], Options(BatchOperation.InstallBootSystem) with { AuxiliaryPath = system });
        Assert.Equal("Skipped incompatible", preview.Rows.Single().Result); Assert.False(Directory.Exists(Path.Combine(root, "out")));
    }

    [Fact]
    public void RecursiveScanAndOutputCollisionsAreExplicit()
    {
        string first = Disk("same.dsk", Fragmented()); Directory.CreateDirectory(Path.Combine(root, "nested")); string second = Path.Combine(root, "nested", "same.dsk"); File.Copy(first, second);
        Assert.Equal(2, BatchProcessService.Scan(root, true, ".dsk").Length); Assert.Single(BatchProcessService.Scan(root, false, "dsk"));
        var preview = BatchProcessService.Preview([first, second], Options()); Assert.All(preview.Rows, r => Assert.Equal("Error", r.Result));
    }

    [Fact]
    public void FolderOutputsPreserveRelativeSubfoldersAndAllowSameBasenames()
    {
        string first = Disk("same.dsk", Fragmented()); Directory.CreateDirectory(Path.Combine(root, "nested"));
        string second = Path.Combine(root, "nested", "same.dsk"); File.Copy(first, second);
        var original = File.ReadAllBytes(first);
        var preview = BatchProcessService.Preview(BatchProcessService.Scan(root, true, ".dsk"), Options() with { InputRoot = root });
        Assert.All(preview.Rows, r => Assert.Equal("Will modify", r.Result));
        Assert.Equal(Path.Combine(root, "out", "nested", "same_processed.dsk"), preview.Rows.Single(r => r.Input == second).Output);
        Assert.False(Directory.Exists(Path.Combine(root, "out")));
        BatchProcessService.Execute(preview);
        Assert.All(preview.Rows, r => { Assert.Equal("Completed", r.Result); Assert.True(File.Exists(r.Output)); });
        Assert.Equal(original, File.ReadAllBytes(first)); Assert.Equal(original, File.ReadAllBytes(second));
    }

    [Fact]
    public void HfeCatalogDistinguishesPhysicalDecodingAndValidatedFilesystemProjection()
    {
        var disk = DskDocumentFactory.CreateFsmz(tracks: 4); disk.FileSystem.Insert("TEST", [1, 2, 3]); disk.MarkModified();
        string path = Path.Combine(root, "known.hfe"); File.WriteAllBytes(path, HfeImage.FromDsk(disk).Serialize(true));
        var original = File.ReadAllBytes(path);
        var preview = BatchProcessService.Preview([path], Options(BatchOperation.Analyze));
        var row = Assert.Single(preview.Rows);
        Assert.Equal("Fsmz", row.Catalog!.Filesystem); Assert.Equal(1, row.Catalog.FileCount);
        Assert.Equal(128, row.Catalog.SectorCount);
        Assert.Contains("read-only projection", row.Filesystem);
        Assert.Contains("Decoded sectors: 128", BatchProcessService.Report(preview, "txt"));
        BatchProcessService.Execute(preview);
        Assert.Equal("Analyzed", row.Result); Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(root, "out")));
    }

    [Fact]
    public void ReplaceRequiresConfirmationAndPreservesBackup()
    {
        string path = Disk("original.dsk", Fragmented()); byte[] original = File.ReadAllBytes(path);
        var preview = BatchProcessService.Preview([path], Options() with { ReplaceOriginals = true });
        Assert.Throws<InvalidOperationException>(() => BatchProcessService.Execute(preview));
        BatchProcessService.Execute(preview, confirmReplace: true); Assert.Equal("Completed", preview.Rows.Single().Result); Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
    }

    [Fact]
    public void HfeConversionRequiresLossConsentAndProducesVerifiedSeparateImage()
    {
        string path = Disk("source.dsk", DskDocumentFactory.CreateRaw(1, 1, 8, 512, 1, 0x4E, 0xE5, "test"));
        var before = File.ReadAllBytes(path); var preview = BatchProcessService.Preview([path], Options(BatchOperation.ConvertFormat));
        Assert.Equal("Will modify", preview.Rows.Single().Result); Assert.Throws<InvalidOperationException>(() => BatchProcessService.Execute(preview));
        BatchProcessService.Execute(preview, confirmLoss: true); Assert.Equal("Completed", preview.Rows.Single().Result);
        Assert.True(HfeImage.Parse(File.ReadAllBytes(preview.Rows.Single().Output)).IsV3); Assert.Equal(before, File.ReadAllBytes(path));
    }

    [PersonalCpmReferenceFact]
    public void NativeBatchBootUsesRegisteredFileBasedInstallerAndRejectsDifferentLayout()
    {
        string native = Disk("native.dsk", DskDocumentFactory.CreatePersonalCpm80(false)); string incompatible = Disk("lec.dsk", DskDocumentFactory.CreateCpm(false));
        var preview = BatchProcessService.Preview([native, incompatible], Options(BatchOperation.InstallBootSystem) with { AuxiliaryPath = PersonalCpmSystemTests.FullReference });
        Assert.Equal("Will modify", preview.Rows.Single(r => r.Input == native).Result); Assert.Equal("Skipped incompatible", preview.Rows.Single(r => r.Input == incompatible).Result);
        BatchProcessService.Execute(preview); var output = DskDocument.Open(preview.Rows.Single(r => r.Input == native).Output);
        Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(output)); Assert.Equal(PersonalCpmSystemInstaller.RegisteredFingerprint, PersonalCpmSystemInstaller.Fingerprint(output));
        var row = preview.Rows.Single(r => r.Input == native);
        Assert.Contains("PCPM.SYS=missing", row.OldBootSystemFingerprint);
        Assert.Contains("SystemFileSha256", row.NewBootSystemFingerprint);
        Assert.NotEmpty(row.NewBootProfile);
        Assert.Contains("Old boot/system fingerprint:", BatchProcessService.Report(preview, "txt"));
    }

    [Fact]
    public void SafeBatchContainerRepairDoesNotOverwriteSource()
    {
        byte[] valid = DskImage.CreateUniform(3, 1, 2, 512, 1, 0x4E, 0xE5, "test").Serialize(); byte[] damaged = (byte[])valid.Clone(); damaged[0x34] = 1;
        string path = Path.Combine(root, "damaged.dsk"); File.WriteAllBytes(path, damaged);
        var preview = BatchProcessService.Preview([path], Options(BatchOperation.SafeRepair)); Assert.Equal("Will modify", preview.Rows.Single().Result);
        BatchProcessService.Execute(preview); Assert.Equal(valid, File.ReadAllBytes(preview.Rows.Single().Output)); Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void BackupCreatedAfterPreviewIsNeverOverwritten()
    {
        string path = Disk("original.dsk", Fragmented()); byte[] original = File.ReadAllBytes(path);
        var preview = BatchProcessService.Preview([path], Options() with { ReplaceOriginals = true }); File.WriteAllBytes(path + ".bak", [99]);
        BatchProcessService.Execute(preview, confirmReplace: true); Assert.Equal("Error", preview.Rows.Single().Result);
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(path + ".bak")); Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    private string Tape(string name = "program.mzf", byte[]? trailing = null)
    {
        string path = Path.Combine(root, name);
        File.WriteAllBytes(path, TapeTestData.CreateMzf(Enumerable.Range(0, 512).Select(i => (byte)(i % 13)).ToArray(), trailing: trailing));
        return path;
    }

    [Theory]
    [InlineData("MZF")]
    [InlineData("M12")]
    [InlineData("MZT")]
    [InlineData("WAV")]
    [InlineData("FLAC")]
    [InlineData("LEP")]
    [InlineData("L16")]
    [InlineData("QD (Sharp)")]
    [InlineData("QD (HxC)")]
    [InlineData("QD (uniform)")]
    [InlineData("QDF")]
    [InlineData("MZQ")]
    public void TapeConversionsReopenVerifyAndExecuteWithoutChangingSource(string target)
    {
        string input = Tape(); byte[] original = File.ReadAllBytes(input);
        var preview = BatchProcessService.Preview([input], Options(BatchOperation.ConvertFormat) with { TargetFormat = target });
        var row = Assert.Single(preview.Rows);
        Assert.True(row.Result == "Will modify", row.Reason);
        Assert.False(Directory.Exists(Path.Combine(root, "out")));
        BatchProcessService.Execute(preview);
        Assert.True(row.Result == "Completed", row.Reason);
        Assert.Equal(original, File.ReadAllBytes(input)); Assert.True(File.Exists(row.Output));
        var integrity = Assert.Single(BatchProcessService.Preview([row.Output], Options(BatchOperation.TestIntegrity)).Rows);
        Assert.True(integrity.Result == "Will analyze", integrity.Reason + "\n" + integrity.DetailedReport);
        Assert.Equal(1, integrity.Catalog!.FileCount);
    }

    [Theory]
    [InlineData((int)MzfCompressionAlgorithm.Zx0, true)]
    [InlineData((int)MzfCompressionAlgorithm.Zx7, false)]
    public void TapeCompressionAndDecompressionRoundTripWithM2i(int algorithm, bool quick)
    {
        string input = Tape("program.m12", [9, 8]);
        File.WriteAllText(Path.ChangeExtension(input, ".m2i"), "TYPE=IC\nSPEED=1:2\n");
        var preview = BatchProcessService.Preview([input], Options(BatchOperation.CompressPrograms) with { Compression = new((MzfCompressionAlgorithm)algorithm, CompressionDirection.Backward, Zx0Quick: quick) });
        var row = Assert.Single(preview.Rows); Assert.True(row.Result == "Will modify", row.Reason);
        BatchProcessService.Execute(preview); Assert.True(row.Result == "Completed", row.Reason);
        Assert.Contains("TYPE=IC", File.ReadAllText(Path.ChangeExtension(row.Output, ".m2i")));
        var packed = new MZTFileReader().ReadStandaloneMzf(row.Output);
        Assert.Equal(new byte[] { 9, 8 }, packed.Body.TrailingData);
        var unpack = BatchProcessService.Preview([row.Output], Options(BatchOperation.DecompressPrograms) with { OutputDirectory = Path.Combine(root, "restored") });
        Assert.True(unpack.Rows[0].Result == "Will modify", unpack.Rows[0].Reason);
        BatchProcessService.Execute(unpack); Assert.Equal("Completed", unpack.Rows[0].Result);
        Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(unpack.Rows[0].Output));
    }

    [Fact]
    public void RecompressingRecognizedInputDoesNotDoubleCompress()
    {
        string input = Tape();
        var packed = MzfCompressionService.Compress(new MZTFileReader().ReadStandaloneMzf(input), new(MzfCompressionAlgorithm.Zx7), CompressionTarget.MzfTape).Record;
        File.WriteAllBytes(input, TapeDocumentWriter.SerializeMzf(packed, true));
        var preview = BatchProcessService.Preview([input], Options(BatchOperation.CompressPrograms) with { Compression = new(MzfCompressionAlgorithm.Zx0, Zx0Quick: true) });
        Assert.True(preview.Rows[0].Result == "Will modify", preview.Rows[0].Reason);
        BatchProcessService.Execute(preview);
        var result = new MZTFileReader().ReadStandaloneMzf(preview.Rows[0].Output);
        Assert.Equal(MzfDecompressionService.Decompress(packed).Record.Body.MzfBody, MzfDecompressionService.Decompress(result).Record.Body.MzfBody);
    }

    [Fact]
    public void SidecarChangesAndLateOutputSidecarCollisionPreventPartialPair()
    {
        string input = Tape("program.m12"); string sidecar = Path.ChangeExtension(input, ".m2i");
        File.WriteAllText(sidecar, "TYPE=IC\nSPEED=1:2\n");
        var options = Options(BatchOperation.ConvertFormat) with { TargetFormat = "MZF" };
        var preview = BatchProcessService.Preview([input], options);
        File.WriteAllText(sidecar, "TYPE=NORMAL\nSPEED=1:1\n");
        BatchProcessService.Execute(preview); Assert.Equal("Error", preview.Rows[0].Result); Assert.False(File.Exists(preview.Rows[0].Output));
        preview = BatchProcessService.Preview([input], options);
        Directory.CreateDirectory(Path.GetDirectoryName(preview.Rows[0].Output)!);
        string outputSidecar = SidecarService.GetSidecarPath(preview.Rows[0].Output); File.WriteAllText(outputSidecar, "existing");
        BatchProcessService.Execute(preview); Assert.Equal("Error", preview.Rows[0].Result);
        Assert.False(File.Exists(preview.Rows[0].Output)); Assert.Equal("existing", File.ReadAllText(outputSidecar));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(outputSidecar)!, "*.tmp*"));
    }

    [Fact]
    public void MultipleTapeProgramsExportAsNumberedFilesWithSidecarsAndKeepTrailingData()
    {
        var record = new MZTFileReader().ReadStandaloneMzf(Tape());
        string input = Path.Combine(root, "two.mzt");
        File.WriteAllBytes(input, TapeDocumentWriter.SerializeMzt([record, record.DeepClone()]).Concat(new byte[] { 9, 8 }).ToArray());
        var preview = BatchProcessService.Preview([input], Options(BatchOperation.ExtractAll));
        Assert.True(preview.Rows[0].Result == "Will modify", preview.Rows[0].Reason);
        BatchProcessService.Execute(preview); Assert.Equal("Completed", preview.Rows[0].Result);
        Assert.Equal(2, Directory.GetFiles(preview.Rows[0].Output, "*.mzf").Length);
        Assert.Equal(2, Directory.GetFiles(preview.Rows[0].Output, "*.mfi").Length);
        Assert.Equal(new byte[] { 9, 8 }, File.ReadAllBytes(Path.Combine(preview.Rows[0].Output, "container-trailing.bin")));
    }

    [Fact]
    public void IntegrityReportsTruncatedTapeAndExplainsChecksumLimits()
    {
        string good = Tape(); string bad = Path.Combine(root, "bad.m12"); File.WriteAllBytes(bad, File.ReadAllBytes(good)[..^1]);
        var preview = BatchProcessService.Preview([good, bad], Options(BatchOperation.TestIntegrity));
        Assert.Equal("Will analyze", preview.Rows[0].Result); Assert.Equal("Error", preview.Rows[1].Result);
        Assert.Contains("no payload checksum", preview.Rows[0].DetailedReport);
        BatchProcessService.Execute(preview); Assert.Equal("Analyzed", preview.Rows[0].Result);
        Assert.False(Directory.Exists(Path.Combine(root, "out")));
        Assert.Contains("programs: 1", BatchProcessService.Report(preview, "txt"));
    }

    [Fact]
    public void UnsupportedExtensionIsRejectedByPolicyAndService()
    {
        string input = Tape();
        Assert.False(BatchMediaPolicy.Supports(BatchOperation.Defragment, input));
        Assert.DoesNotContain("mzf", BatchMediaPolicy.PickerFilter(BatchOperation.Defragment));
        Assert.DoesNotContain("All files", BatchMediaPolicy.PickerFilter(BatchOperation.Defragment));
        Assert.Equal("Skipped incompatible", BatchProcessService.Preview([input], Options()).Rows[0].Result);
        Assert.True(BatchMediaPolicy.Supports(BatchOperation.CompressPrograms, "recording.flac"));
        Assert.False(BatchMediaPolicy.Supports(BatchOperation.ConvertFormat, input, "HFEv3"));
        Assert.True(BatchMediaPolicy.Supports(BatchOperation.ConvertFormat, input, "WAV"));
        Assert.False(BatchMediaPolicy.Supports(BatchOperation.ConvertFormat, "disk.dsk", "FLAC"));
    }

    [Fact]
    public void BatchTapeDoesNotSilentlyDiscardTrailingBytesOrReplaceOriginals()
    {
        string input = Tape(trailing: [1, 2]);
        var options = Options(BatchOperation.ConvertFormat) with { TargetFormat = "QDF" };
        Assert.Equal("Skipped incompatible", BatchProcessService.Preview([input], options).Rows[0].Result);
        Assert.Equal("Skipped incompatible", BatchProcessService.Preview([input], options with { TargetFormat = "M12", ReplaceOriginals = true }).Rows[0].Result);
        Assert.False(Directory.Exists(Path.Combine(root, "out")));
    }

    [Theory]
    [InlineData("WAV")]
    [InlineData("FLAC")]
    [InlineData("QD (HxC)")]
    [InlineData("QDF")]
    [InlineData("MZQ")]
    public void AudioAndQuickDiskInputsConvertBackToM12(string target)
    {
        string input = Tape();
        var first = BatchProcessService.Preview([input], Options(BatchOperation.ConvertFormat) with { TargetFormat = target });
        BatchProcessService.Execute(first); Assert.Equal("Completed", first.Rows[0].Result);
        var second = BatchProcessService.Preview([first.Rows[0].Output], Options(BatchOperation.ConvertFormat) with { TargetFormat = "M12", OutputDirectory = Path.Combine(root, "roundtrip") });
        Assert.True(second.Rows[0].Result == "Will modify", second.Rows[0].Reason);
        BatchProcessService.Execute(second); Assert.Equal("Completed", second.Rows[0].Result);
        Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(second.Rows[0].Output));
        Assert.True(File.Exists(Path.ChangeExtension(second.Rows[0].Output, ".m2i")));
    }

    [Fact]
    public void QuickDiskCompressionKeepsItsContainerAndExtractsRestorablePrograms()
    {
        var record = new MZTFileReader().ReadStandaloneMzf(Tape());
        string input = Path.Combine(root, "program.qd"); File.WriteAllBytes(input, QdImageReaderWriter.Write([record, record.DeepClone()], QdImageFormat.HxcPhysical));
        var preview = BatchProcessService.Preview([input], Options(BatchOperation.CompressPrograms) with { Compression = new(MzfCompressionAlgorithm.Zx0, Zx0Quick: true) });
        Assert.True(preview.Rows[0].Result == "Will modify", preview.Rows[0].Reason);
        BatchProcessService.Execute(preview); Assert.Equal("Completed", preview.Rows[0].Result);
        var result = QdImageReaderWriter.ReadFile(preview.Rows[0].Output);
        Assert.Equal(QdImageFormat.HxcPhysical, result.Format); Assert.Equal(2, result.Records.Count);
        foreach (var program in result.Records) Assert.Equal(record.Body.MzfBody, MzfDecompressionService.Decompress(program).Record.Body.MzfBody);
    }

    [Fact]
    public void IntegrityRejectsBrokenCrcAndMalformedSidecar()
    {
        var record = new MZTFileReader().ReadStandaloneMzf(Tape());
        byte[] qdf = QDFFileReader.BuildImage([record]);
        // A body payload occurrence is unambiguous in this synthetic image.
        int body = qdf.AsSpan().IndexOf(record.Body.MzfBody); Assert.True(body >= 0); qdf[body] ^= 0x20;
        string corrupt = Path.Combine(root, "corrupt.qdf"); File.WriteAllBytes(corrupt, qdf);
        Assert.Equal("Error", BatchProcessService.Preview([corrupt], Options(BatchOperation.TestIntegrity)).Rows[0].Result);
        string input = Tape("sidecar.m12"); File.WriteAllText(Path.ChangeExtension(input, ".m2i"), "TYPE=invalid\n");
        var malformed = BatchProcessService.Preview([input], Options(BatchOperation.TestIntegrity));
        Assert.Equal("Error", malformed.Rows[0].Result); Assert.Contains("sidecar", malformed.Rows[0].Reason);
    }

    [Fact]
    public void QuickDiskHeaderLossIsReportedAndEmbeddedLoadersAreRejected()
    {
        string input = Tape(); byte[] bytes = File.ReadAllBytes(input); bytes[100] = 65; File.WriteAllBytes(input, bytes);
        var options = Options(BatchOperation.ConvertFormat) with { TargetFormat = "QDF" };
        var preview = BatchProcessService.Preview([input], options);
        Assert.True(preview.Rows[0].Result == "Will modify", preview.Rows[0].Reason);
        Assert.True(preview.Rows[0].RequiresLossConfirmation); Assert.Contains("description", preview.Rows[0].Warnings);
        Assert.Throws<InvalidOperationException>(() => BatchProcessService.Execute(preview));
        Assert.False(Directory.Exists(Path.Combine(root, "out")));
        var packed = MzfCompressionService.Compress(new MZTFileReader().ReadStandaloneMzf(input), new(MzfCompressionAlgorithm.Zx7, Zx7EmbeddedLoader: true), CompressionTarget.MzfTape).Record;
        File.WriteAllBytes(input, TapeDocumentWriter.SerializeMzf(packed, true));
        var embedded = BatchProcessService.Preview([input], options);
        Assert.Equal("Skipped incompatible", embedded.Rows[0].Result); Assert.Contains("executable MZF header", embedded.Rows[0].Reason);
    }

    [Fact]
    public void MixedCatalogAndIntegrityReportProgressWithoutWritingOutputs()
    {
        string tape = Tape(); string disk = Disk("disk.dsk");
        var events = new List<BatchProgress>();
        var progress = new ImmediateProgress(events);
        var preview = BatchProcessService.Preview([tape, disk], Options(BatchOperation.Analyze), progress);
        Assert.All(preview.Rows, row => Assert.Equal("Will analyze", row.Result));
        Assert.Equal(2, events[^1].Completed); Assert.Equal(2, events[^1].Total);
        BatchProcessService.Execute(preview, progress: progress);
        Assert.All(preview.Rows, row => Assert.Equal("Analyzed", row.Result));
        Assert.Equal(2, events[^1].Completed); Assert.False(Directory.Exists(Path.Combine(root, "out")));
    }

    private sealed class ImmediateProgress(List<BatchProgress> events) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => events.Add(value);
    }

    private sealed class CallbackProgress(Action<BatchProgress> callback) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => callback(value);
    }

    [Fact]
    public void CancellingPreviewKeepsResultsButDisablesExecution()
    {
        string first = Tape("first.mzf"); string second = Tape("second.mzf");
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(value => { if (value.Stage == "Preparing" && value.Completed == 1) cancellation.Cancel(); });
        var preview = BatchProcessService.Preview([first, second], Options(BatchOperation.CompressPrograms) with { Compression = new(MzfCompressionAlgorithm.Zx7) }, progress, cancellation.Token);
        Assert.True(preview.Cancelled); Assert.Equal("Will modify", preview.Rows[0].Result); Assert.Equal("Cancelled", preview.Rows[1].Result);
        Assert.Throws<InvalidOperationException>(() => BatchProcessService.Execute(preview)); Assert.False(Directory.Exists(Path.Combine(root, "out")));
        Assert.Contains("cancelled: 1", preview.Summary);
    }

    [Fact]
    public void CancellingExecutionKeepsCommittedOutputsAndStopsRemainingFiles()
    {
        var paths = new[] { Tape("first.mzf"), Tape("second.mzf"), Tape("third.mzf") };
        var preview = BatchProcessService.Preview(paths, Options(BatchOperation.ConvertFormat) with { TargetFormat = "M12" });
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress(value => { if (value.Stage == "Processed" && value.Completed == 1) cancellation.Cancel(); });
        BatchProcessService.Execute(preview, progress: progress, cancellationToken: cancellation.Token);
        Assert.True(preview.Cancelled); Assert.Equal("Completed", preview.Rows[0].Result);
        Assert.All(preview.Rows.Skip(1), row => { Assert.Equal("Cancelled", row.Result); Assert.False(File.Exists(row.Output)); });
        Assert.True(File.Exists(preview.Rows[0].Output)); Assert.True(File.Exists(SidecarService.GetSidecarPath(preview.Rows[0].Output)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "out"), "*.tmp*"));
    }

    [Fact]
    public void CancellingBeforeCommitRemovesBothStagedTapeFiles()
    {
        string input = Tape(); byte[] original = File.ReadAllBytes(input);
        var preview = BatchProcessService.Preview([input], Options(BatchOperation.ConvertFormat) with { TargetFormat = "M12" });
        var row = preview.Rows[0]; var verify = row.VerifyOutput!;
        using var cancellation = new CancellationTokenSource();
        row.VerifyOutput = (bytes, token) => { verify(bytes, token); cancellation.Cancel(); };
        BatchProcessService.Execute(preview, cancellationToken: cancellation.Token);
        Assert.Equal("Cancelled", row.Result); Assert.False(File.Exists(row.Output)); Assert.False(File.Exists(SidecarService.GetSidecarPath(row.Output)));
        Assert.Equal(original, File.ReadAllBytes(input)); Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "out")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioDecoderChoiceIsUsedAndRecorded(bool heuristic)
    {
        string input = Tape(); var wave = BatchProcessService.Preview([input], Options(BatchOperation.ConvertFormat) with { TargetFormat = "WAV" }); BatchProcessService.Execute(wave);
        var preview = BatchProcessService.Preview([wave.Rows[0].Output], Options(BatchOperation.TestIntegrity) with { UseHeuristicAnalysis = heuristic });
        Assert.Equal("Passed", preview.Rows[0].Integrity);
        Assert.Contains(preview.Rows[0].IntegrityChecks, c => c.Check == "Tape / frame checksum" && c.Result == "Passed");
        Assert.Contains(heuristic ? "Decoder: heuristic analysis" : "Decoder: standard", preview.Rows[0].DetailedReport);
        Assert.Equal(heuristic ? "Heuristic analysis" : "Standard", preview.Rows[0].DetailFields.Single(f => f.Property == "Decoder").Value);
        var contents = Assert.Single(preview.Rows[0].Contents);
        var original = new MZTFileReader().ReadStandaloneMzf(input);
        Assert.Equal(SharpMzEncoding.ConvertMzfNameToASCIIString(original.Header.MzfFname), contents.Name);
        Assert.Equal(original.Header.MzfStart, contents.Load);
        Assert.Equal(original.Header.MzfExec, contents.Exec);
        Assert.Equal(original.Body.MzfBody.Length, contents.Size);
    }

    [Fact]
    public void CatalogProvidesStructuredContentsForTapeAndDiskWithoutParsingReports()
    {
        var record = new MZTFileReader().ReadStandaloneMzf(Tape());
        var second = record.DeepClone(); var header = second.Header;
        header.MzfStart = 0x3456; header.MzfExec = 0x4567; second.Header = header;
        string container = Path.Combine(root, "two.mzt");
        File.WriteAllBytes(container, TapeDocumentWriter.SerializeMzt([record, second]));
        var disk = DskDocumentFactory.CreateCpm(false);
        disk.FileSystem.Insert("TEST.COM", [1, 2, 3], user: 3); disk.MarkModified();
        var rows = BatchProcessService.Preview([container, Disk("contents.dsk", disk)], Options(BatchOperation.Analyze)).Rows;
        Assert.Equal(new[] { 1, 2 }, rows[0].Contents.Select(c => c.Number));
        Assert.Equal((ushort)0x3456, rows[0].Contents[1].Load);
        Assert.Equal((ushort)0x4567, rows[0].Contents[1].Exec);
        var file = Assert.Single(rows[1].Contents);
        Assert.Equal("TEST.COM", file.Name); Assert.Equal(3, file.User);
        Assert.Equal(disk.FileSystem.ReadDirectory()[0].Size, file.Size);
        rows[0].DetailedReport = "";
        Assert.Equal(2, rows[0].Contents.Count);
        using var report = JsonDocument.Parse(BatchProcessService.Report(new BatchPreview(Options(BatchOperation.Analyze), rows), "json"));
        Assert.Equal(2, report.RootElement[0].GetProperty("Contents").GetArrayLength());
    }

    [Theory]
    [InlineData("BIN", ".bin")]
    [InlineData("M12", ".m12")]
    [InlineData("MZT", ".mzt")]
    [InlineData("WAV", ".wav")]
    [InlineData("FLAC", ".flac")]
    public void ExtractAllUsesChosenProgramFormat(string target, string extension)
    {
        string input = Tape(); var record = new MZTFileReader().ReadStandaloneMzf(input);
        string container = Path.Combine(root, "source.mzt"); File.WriteAllBytes(container, TapeDocumentWriter.SerializeMzt([record, record.DeepClone()]));
        var preview = BatchProcessService.Preview([container], Options(BatchOperation.ExtractAll) with { ExtractFormat = target });
        Assert.True(preview.Rows[0].Result == "Will modify", preview.Rows[0].Reason);
        BatchProcessService.Execute(preview); Assert.Equal("Completed", preview.Rows[0].Result);
        Assert.Equal(2, Directory.GetFiles(preview.Rows[0].Output, "*" + extension).Length);
        if (target == "M12") Assert.Equal(2, Directory.GetFiles(preview.Rows[0].Output, "*.m2i").Length);
        if (target == "BIN") foreach (var file in Directory.GetFiles(preview.Rows[0].Output)) Assert.Equal(record.Body.MzfBody, File.ReadAllBytes(file));
    }

    [Fact]
    public void DiskExtractionUsesStoredTapeMetadataAndRejectsGuessingCpmHeaders()
    {
        var disk = DskDocumentFactory.CreateFsmz(); disk.FileSystem.Insert("PROGRAM", [1, 2, 3], 1, 0x1200, 0x1200); disk.MarkModified();
        var preview = BatchProcessService.Preview([Disk("fsmz.dsk", disk)], Options(BatchOperation.ExtractAll) with { ExtractFormat = "M12" });
        Assert.True(preview.Rows[0].Result == "Will modify", preview.Rows[0].Reason);
        BatchProcessService.Execute(preview); Assert.Equal("Completed", preview.Rows[0].Result);
        Assert.Equal(new byte[] { 1, 2, 3 }, new MZTFileReader().ReadStandaloneMzf(Directory.GetFiles(preview.Rows[0].Output, "*.m12").Single()).Body.MzfBody);
        var cpm = DskDocumentFactory.CreateCpm(false); cpm.FileSystem.Insert("TEST.COM", [1, 2, 3]); cpm.MarkModified();
        var incompatible = BatchProcessService.Preview([Disk("cpm.dsk", cpm)], Options(BatchOperation.ExtractAll) with { ExtractFormat = "M12" });
        Assert.Equal("Skipped incompatible", incompatible.Rows[0].Result); Assert.Contains("no tape header is guessed", incompatible.Rows[0].Reason);
    }

    [Fact]
    public void CatalogDoesNotClaimIntegrityAndIntegrityHasSeparateReportField()
    {
        string path = Tape();
        var info = BatchProcessService.Preview([path], Options(BatchOperation.Analyze));
        var test = BatchProcessService.Preview([path], Options(BatchOperation.TestIntegrity));
        Assert.Equal("Not tested", info.Rows[0].Integrity); Assert.Equal("Passed", test.Rows[0].Integrity);
        Assert.Empty(info.Rows[0].IntegrityChecks);
        Assert.Contains(test.Rows[0].IntegrityChecks, c => c.Check == "Header / body length" && c.Result == "Passed");
        Assert.Contains(test.Rows[0].IntegrityChecks, c => c.Check == "Tape / frame checksum" && c.Result == "Not available");
        Assert.Contains("Test integrity", info.Rows[0].Reason);
        Assert.Contains("integrity: Passed", BatchProcessService.Report(test, "txt"));
        Assert.Contains("integrity", BatchProcessService.Report(test, "csv").Split('\n')[0]);
        Assert.Contains("\"Integrity\": \"Passed\"", BatchProcessService.Report(test, "json"));
    }

    [Fact]
    public void IntegrityTestsCompressedStreamAndCatalogUsesReadableTapeProfile()
    {
        string path = Tape(); var record = new MZTFileReader().ReadStandaloneMzf(path);
        record.Profile = TapeProfile.Tc1_2;
        File.WriteAllBytes(path, TapeDocumentWriter.SerializeMzf(record, true));
        File.WriteAllBytes(SidecarService.GetSidecarPath(path), SidecarService.SerializeMfi(record));
        var catalog = BatchProcessService.Preview([path], Options(BatchOperation.Analyze)).Rows[0];
        Assert.Equal("TC 1:2", Assert.Single(catalog.Contents).Profile);
        Assert.Contains("TC 1:2", catalog.DetailedReport);
        var packed = MzfCompressionService.Compress(record, new(MzfCompressionAlgorithm.Zx7), CompressionTarget.MzfTape).Record;
        File.WriteAllBytes(path, TapeDocumentWriter.SerializeMzf(packed, true));
        File.WriteAllBytes(SidecarService.GetSidecarPath(path), SidecarService.SerializeMfi(packed));
        var info = BatchProcessService.Preview([path], Options(BatchOperation.Analyze)).Rows[0];
        var tested = BatchProcessService.Preview([path], Options(BatchOperation.TestIntegrity)).Rows[0];
        Assert.Empty(info.IntegrityChecks);
        Assert.DoesNotContain("Recognized compression stream decoded", info.DetailedReport);
        Assert.Contains(tested.IntegrityChecks, c => c.Check == "Compressed stream" && c.Result == "Passed");
        Assert.Contains("Recognized compression stream decoded", tested.DetailedReport);
    }

    [Fact]
    public void DiskIntegrityProvidesCheckResultsAndUnreadableSourceProvidesFailure()
    {
        string path = Disk("checks.dsk");
        var info = BatchProcessService.Preview([path], Options(BatchOperation.Analyze)).Rows[0];
        var tested = BatchProcessService.Preview([path], Options(BatchOperation.TestIntegrity)).Rows[0];
        Assert.Empty(info.IntegrityChecks);
        Assert.Contains(tested.IntegrityChecks, c => c.Check == "Container / filesystem" && c.Result == "Passed");
        Assert.Contains(tested.IntegrityChecks, c => c.Check == "Sector CRC" && c.Result == "Not available");
        File.WriteAllBytes(path, [0, 1]);
        var failed = BatchProcessService.Preview([path], Options(BatchOperation.TestIntegrity)).Rows[0];
        Assert.Equal("Failed", failed.Integrity);
        Assert.Contains(failed.IntegrityChecks, c => c.Result == "Failed" && c.Details.Length > 0);
    }
}
