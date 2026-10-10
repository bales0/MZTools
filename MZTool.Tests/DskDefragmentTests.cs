namespace MZTools.Tests;

public class DskDefragmentTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void StaleFilesystemRepairLeavesTargetUnchanged(int kind)
    {
        var document = kind switch { 0 => DskDocumentFactory.CreateFsmz(), 1 => DskDocumentFactory.CreateMrs(), _ => DskDocumentFactory.CreatePersonalCpm80(false) };
        if (kind == 2) { document.FileSystem.Insert("PCPM.SYS", new byte[2048]); document.MarkModified(); }
        var action = DskFilesystemRepairService.For(document); var preview = action.Preview(document);
        document.Image.Tracks[^1]!.Sectors[^1].Data[0] ^= 1; document.MarkModified();
        var before = document.Serialize();
        Assert.Throws<InvalidOperationException>(() => action.Apply(document, preview));
        Assert.Equal(before, document.Serialize()); Assert.True(document.IsModified);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DefragmentPreservesPayloadsAndMetadata(int kind)
    {
        var document = kind switch { 0 => DskDocumentFactory.CreateFsmz(), 1 => DskDocumentFactory.CreateCpm(false), 2 => DskDocumentFactory.CreateMrs(), _ => DskDocumentFactory.CreatePersonalCpm80(false) };
        document.FileSystem.Insert("GAP", new byte[1024]);
        document.FileSystem.Insert("FIRST", Enumerable.Repeat((byte)0x21, 512).ToArray(), 1, 0x1234, 0x5678);
        document.FileSystem.Insert("SECOND", Enumerable.Repeat((byte)0x42, 512).ToArray(), 1, 0x3456, 0x789A);
        document.FileSystem.Delete(document.FileSystem.ReadDirectory().Single(e => e.Name == "GAP"));
        if (kind == 3) document.FileSystem.Insert("PCPM.SYS", new byte[2048]);
        foreach (var entry in document.FileSystem.ReadDirectory())
        {
            if (document.FileSystem is CpmFileSystem cpm) cpm.SetAttributes(entry, true, true, true);
            if (document.FileSystem is FsmzFileSystem fsmz) fsmz.SetLocked(entry, true);
        }
        document.MarkModified();
        var before = document.Serialize(); var files = document.FileSystem.ReadDirectory().Select(e => (e, document.FileSystem.Extract(e))).ToArray();
        var preview = DskDefragmentService.Preview(document);
        Assert.Contains("old allocation blocks", preview.Report);
        Assert.Contains("new allocation blocks", preview.Report);
        Assert.Contains("Files moved:", preview.Report);
        Assert.Contains("Largest free region:", preview.Report);
        Assert.Contains("Payload verification: passed", preview.Report);
        Assert.Contains("Metadata verification: passed", preview.Report);
        Assert.Equal(before, document.Serialize());
        preview.Apply(document);
        foreach (var (old, payload) in files)
        {
            var current = document.FileSystem.ReadDirectory().Single(e => e.Name == old.Name && e.Extension == old.Extension && e.User == old.User);
            Assert.Equal(payload, document.FileSystem.Extract(current));
            Assert.Equal(old.Locked, current.Locked); Assert.Equal(old.ReadOnly, current.ReadOnly);
            Assert.Equal(old.System, current.System); Assert.Equal(old.Archived, current.Archived);
            Assert.Equal(old.LoadAddress, current.LoadAddress); Assert.Equal(old.ExecuteAddress, current.ExecuteAddress);
        }
        Assert.Equal(0, DskAnalyzer.Analyze(document).Errors);
        Assert.Equal(document.Serialize(), DskDefragmentService.Preview(document).Result);
        if (kind == 3) Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(document));
    }

    [Fact]
    public void FsmzPropertiesChangeOnlySelectedFieldsAndKeepPayload()
    {
        var document = DskDocumentFactory.CreateFsmz(); document.FileSystem.Insert("TEST", [1, 2, 3]); document.MarkModified();
        var entry = document.FileSystem.ReadDirectory().Single(); var original = document.Serialize();
        var changed = DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry) with { FileType = 2, Load = 0x8000, Execute = 0x8100, Locked = true });
        Assert.Equal(new byte[] { 1, 2, 3 }, document.FileSystem.Extract(changed));
        Assert.Equal(2, changed.FileType); Assert.True(changed.Locked); Assert.Equal(0x8000, changed.LoadAddress); Assert.Equal(0x8100, changed.ExecuteAddress);
        var after = document.Serialize(); var sector = DskAnalyzer.Analyze(document).Sectors.Single(s => s.Track == 0 && s.R == 1);
        int slot = (int)sector.FileOffset + 32;
        Assert.All(Enumerable.Range(0, original.Length).Where(i => original[i] != after[i]), i => Assert.Contains(i - slot, new[] { 0, 18, 22, 23, 24, 25 }));
    }

    [Fact]
    public void SafeBitmapRepairAddsClaimsAndPreservesOrphanData()
    {
        var document = DskDocumentFactory.CreateFsmz(); document.FileSystem.Insert("TEST", new byte[512]); document.MarkModified();
        var entry = document.FileSystem.ReadDirectory().Single();
        var device = new DskBlockDevice(document.Image); var info = device.ReadSector(1, 16, true);
        info[6] = 0x80; // Remove file claims; retain an orphan bit.
        device.WriteSector(1, 16, info, true); document.ReplaceContents(document.Image.Serialize());
        var before = document.Serialize(); var action = new FsmzAllocationRepair(); var preview = action.Preview(document);
        Assert.Equal(before, document.Serialize()); action.Apply(document, preview);
        Assert.Equal(0x83, new DskBlockDevice(document.Image).ReadSector(1, 16, true)[6]);
        Assert.Contains(DskAnalyzer.Analyze(document).Issues, i => i.Code == "FSMZ_ORPHAN_BLOCK");
        Assert.Equal(0, DskAnalyzer.Analyze(document).Errors);
    }

    [Fact]
    public void AmbiguousDefragmentFailureLeavesOriginalIdentical()
    {
        var document = DskDocumentFactory.CreateFsmz(); document.FileSystem.Insert("TEST", [1]); document.MarkModified();
        var device = new DskBlockDevice(document.Image); var info = device.ReadSector(1, 16, true); info[6] |= 0x80; device.WriteSector(1, 16, info, true); document.ReplaceContents(document.Image.Serialize());
        var before = document.Serialize(); Assert.Throws<InvalidDataException>(() => DskDefragmentService.Preview(document)); Assert.Equal(before, document.Serialize());
    }

    [Fact]
    public void MrsCountRepairChangesOnlyCountFieldsWithKnownOwnership()
    {
        var document = DskDocumentFactory.CreateMrs(); document.FileSystem.Insert("TEST.BIN", [1, 2, 3]); document.MarkModified();
        var device = new DskBlockDevice(document.Image); var directory = device.ReadLinear512Block(39, true); directory[14] = 2; device.WriteLinear512Block(39, directory, true); document.ReplaceContents(document.Image.Serialize());
        var before = document.Serialize(); var action = new MrsBlockCountRepair(); var preview = action.Preview(document); Assert.Equal(before, document.Serialize()); action.Apply(document, preview);
        Assert.Equal(1, document.FileSystem.ReadDirectory().Single().Blocks); Assert.Equal(0, DskAnalyzer.Analyze(document).Errors);
    }

    [Fact]
    public void NativeDirectoryRepairKeepsEveryFileAndAllocation()
    {
        var document = DskDocumentFactory.CreatePersonalCpm80(false); document.FileSystem.Insert("FIRST.COM", new byte[128]); document.FileSystem.Insert("PCPM.SYS", new byte[2048]); document.MarkModified();
        var fs = (CpmFileSystem)document.FileSystem; var system = fs.ReadRawDirectoryEntry(1); var other = fs.ReadRawDirectoryEntry(0); var before = document.Serialize();
        var action = new PersonalCpmDirectoryRepair(); var preview = action.Preview(document); Assert.Equal(before, document.Serialize()); action.Apply(document, preview);
        fs = (CpmFileSystem)document.FileSystem; Assert.Equal(system, fs.ReadRawDirectoryEntry(0)); Assert.Equal(other, fs.ReadRawDirectoryEntry(1)); Assert.True(PersonalCpmSystemInstaller.IsSystemFirst(document));
    }
}
