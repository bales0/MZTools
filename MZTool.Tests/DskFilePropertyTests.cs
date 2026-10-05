namespace MZTools.Tests;

public class DskFilePropertyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BatchChangesPreserveEveryPayloadAndUnselectedProperties(bool mrs)
    {
        var document = mrs ? DskDocumentFactory.CreateMrs() : DskDocumentFactory.CreateCpm(false);
        for (int i = 0; i < 3; i++) document.FileSystem.Insert($"FILE{i}.BIN", [(byte)i, 42], loadAddress: (ushort)(0x1000 + i), executeAddress: 0x2000);
        document.MarkModified();
        document = DskDocument.Open(document.Serialize());
        var entries = document.FileSystem.ReadDirectory();
        var payloads = entries.Select(document.FileSystem.Extract).ToArray();
        var changes = entries.Take(2).Select(entry => (entry, mrs
            ? DskFileProperties.From(entry) with { Load = 0xABCD }
            : DskFileProperties.From(entry) with { User = 7, ReadOnly = true })).ToArray();
        var updated = DskFilePropertyService.ApplyMany(document, changes);
        Assert.Equal(2, updated.Count); Assert.True(document.IsModified);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(payloads[i], document.FileSystem.Extract(updated[i]));
            if (mrs) { Assert.Equal(0xABCD, updated[i].LoadAddress); Assert.Equal(entries[i].ExecuteAddress, updated[i].ExecuteAddress); }
            else { Assert.Equal(7, updated[i].User); Assert.True(updated[i].ReadOnly); Assert.Equal(entries[i].System, updated[i].System); }
        }
        var untouched = document.FileSystem.ReadDirectory().Single(entry => entry.Key == entries[2].Key);
        Assert.Equal(DskFileProperties.From(entries[2]), DskFileProperties.From(untouched));
        Assert.Equal(payloads[2], document.FileSystem.Extract(untouched));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailureOnSecondBatchFileRollsBackFirstFileAndModifiedState(bool invalidUser)
    {
        var document = DskDocumentFactory.CreateCpm(false);
        document.FileSystem.Insert("FIRST.BIN", [1]); document.FileSystem.Insert("SECOND.BIN", [2]);
        document.FileSystem.Insert("SECOND.BIN", [3], user: 7);
        document.MarkModified();
        document = DskDocument.Open(document.Serialize());
        var entries = document.FileSystem.ReadDirectory().Where(entry => entry.User == 0).ToArray();
        byte[] before = document.Serialize();
        var changes = entries.Select(entry => (entry, DskFileProperties.From(entry) with
            { User = invalidUser && entry.Name == "SECOND" ? 16 : 7, ReadOnly = true })).ToArray();
        Assert.ThrowsAny<Exception>(() => DskFilePropertyService.ApplyMany(document, changes));
        Assert.Equal(before, document.Serialize()); Assert.False(document.IsModified);
    }
    [Theory]
    [InlineData(true, false, false, 0)] [InlineData(false, true, false, 0)]
    [InlineData(false, false, true, 0)] [InlineData(true, true, true, 7)]
    public void CpmPropertiesUpdateAllExtentsAndOnlyNativeAttributeBytes(bool ro, bool sys, bool arc, int user)
    {
        var document = DskDocumentFactory.CreateCpm(false);
        document.FileSystem.Insert("LARGE.BIN", Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray());
        document.FileSystem.Insert("OTHER.COM", [1, 2, 3]); document.MarkModified();
        var entry = document.FileSystem.ReadDirectory().Single(e => e.Name == "LARGE"); Assert.True(entry.Extents > 1);
        byte[] before = document.Serialize(), data = document.FileSystem.Extract(entry);
        var map = DskAnalyzer.Analyze(document); var permitted = new HashSet<int>();
        foreach (var sector in map.Sectors.Where(s => s.Role.HasFlag(DskSectorRole.Directory)))
        for (int i = 0; i < sector.DataLength; i++)
            if (i % 32 is 0 or 9 or 10 or 11) permitted.Add((int)sector.FileOffset + i);
        var updated = DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry) with { User = user, ReadOnly = ro, System = sys, Archived = arc });
        Assert.Equal(user, updated.User); Assert.Equal(ro, updated.ReadOnly); Assert.Equal(sys, updated.System); Assert.Equal(arc, updated.Archived);
        var after = document.Serialize();
        for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) Assert.Contains(i, permitted);
        var reopened = DskDocument.Open(after); var actual = reopened.FileSystem.ReadDirectory().Single(e => e.Name == "LARGE");
        Assert.Equal(data, reopened.FileSystem.Extract(actual)); Assert.Equal(entry.Extents, actual.Extents); Assert.Equal(entry.Blocks, actual.Blocks);
        Assert.Equal(user, actual.User); Assert.Equal(ro, actual.ReadOnly); Assert.Equal(sys, actual.System); Assert.Equal(arc, actual.Archived);
        Assert.Equal(2, reopened.FileSystem.ReadDirectory().Count); Assert.Equal(0, DskAnalyzer.Analyze(reopened).Errors);
        // Check every raw extent, not only the grouped logical file's first flags.
        foreach (var sector in DskAnalyzer.Analyze(reopened).Sectors.Where(s => s.Role.HasFlag(DskSectorRole.Directory)))
        for (int i = 0; i < sector.DataLength; i += 32)
        {
            var raw = sector.Data.AsSpan(i, 32);
            if ((raw[1] & 0x7F) != (byte)'L') continue;
            Assert.Equal(user, raw[0]); Assert.Equal(ro, (raw[9] & 0x80) != 0); Assert.Equal(sys, (raw[10] & 0x80) != 0); Assert.Equal(arc, (raw[11] & 0x80) != 0);
        }
    }

    [Fact]
    public void CpmUserCollisionIsRejectedBeforeAnyChange()
    {
        var document = DskDocumentFactory.CreateCpm(false);
        document.FileSystem.Insert("TEST.BIN", [1], user: 0); document.FileSystem.Insert("TEST.BIN", [2], user: 5); document.MarkModified();
        var entry = document.FileSystem.ReadDirectory().Single(e => e.User == 0); var before = document.Serialize();
        Assert.Throws<IOException>(() => DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry) with { User = 5, ReadOnly = true }));
        Assert.Equal(before, document.Serialize()); Assert.True(document.IsModified);
        Assert.Throws<IOException>(() => ((CpmFileSystem)document.FileSystem).UpdateAttributes(entry, 5, true, true, true));
        Assert.Equal(before, document.Serialize());
    }

    [Theory]
    [InlineData(-1)] [InlineData(16)]
    public void CpmInvalidUserIsRejected(int user)
    {
        var document = DskDocumentFactory.CreateCpm(false); document.FileSystem.Insert("TEST", [1]); document.MarkModified();
        var entry = document.FileSystem.ReadDirectory()[0]; var before = document.Serialize();
        Assert.Throws<ArgumentOutOfRangeException>(() => DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry) with { User = user }));
        Assert.Equal(before, document.Serialize());
    }

    [Fact]
    public void MrsAddressesChangeOnlyFourDirectoryBytesAndSurviveReopen()
    {
        var document = DskDocumentFactory.CreateMrs();
        document.FileSystem.Insert("TEST.BIN", Enumerable.Range(0, 1400).Select(i => (byte)i).ToArray(), loadAddress: 0x1122, executeAddress: 0x3344);
        document.FileSystem.Insert("OTHER.COM", [1, 2, 3]); document.MarkModified();
        var entry = document.FileSystem.ReadDirectory()[0]; byte[] data = document.FileSystem.Extract(entry), before = document.Serialize();
        int slot = int.Parse(entry.Key), block = 39 + slot * 32 / 512, offset = slot * 32 % 512;
        var sector = DskAnalyzer.Analyze(document).Sectors.Single(s => s.Track == block / 9 && s.R == block % 9 + 1);
        var permitted = new[] { 12, 13, 22, 23 }.Select(i => (int)sector.FileOffset + offset + i).ToArray();
        DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry) with { Load = 0xABCD, Execute = 0x5678 });
        byte[] after = document.Serialize(); Assert.Equal(4, before.Where((b, i) => b != after[i]).Count());
        for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) Assert.Contains(i, permitted);
        var reopened = DskDocument.Open(after); var actual = reopened.FileSystem.ReadDirectory()[0];
        Assert.Equal(0xABCD, actual.LoadAddress); Assert.Equal(0x5678, actual.ExecuteAddress);
        Assert.Equal(entry.StartBlock, actual.StartBlock); Assert.Equal(entry.Blocks, actual.Blocks); Assert.Equal(data, reopened.FileSystem.Extract(actual));
        Assert.Equal(0, DskAnalyzer.Analyze(reopened).Errors);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NoOpPropertiesDoNotMarkSavedDocumentModified(bool mrs)
    {
        var original = mrs ? DskDocumentFactory.CreateMrs() : DskDocumentFactory.CreateCpm(false); original.FileSystem.Insert("TEST", [1]); original.MarkModified();
        var document = DskDocument.Open(original.Serialize()); var entry = document.FileSystem.ReadDirectory()[0];
        var before = document.Serialize(); DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry));
        Assert.False(document.IsModified); Assert.Equal(before, document.Serialize());
    }

    [Fact]
    public void CpmPropertiesCanClearAllFlagsAndRetainPayload()
    {
        var document = DskDocumentFactory.CreateCpm(false); document.FileSystem.Insert("TEST", new byte[70000]); document.MarkModified();
        var entry = document.FileSystem.ReadDirectory()[0]; var data = document.FileSystem.Extract(entry);
        entry = DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry) with { ReadOnly = true, System = true, Archived = true });
        entry = DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry) with { ReadOnly = false, System = false, Archived = false });
        Assert.False(entry.ReadOnly); Assert.False(entry.System); Assert.False(entry.Archived);
        Assert.Equal(data, document.FileSystem.Extract(entry));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StaleFileSelectionIsRejectedWithoutWriting(bool mrs)
    {
        var document = mrs ? DskDocumentFactory.CreateMrs() : DskDocumentFactory.CreateCpm(false);
        document.FileSystem.Insert("TEST", [1]); var entry = document.FileSystem.ReadDirectory()[0];
        document.FileSystem.Delete(entry); document.MarkModified(); var before = document.Serialize();
        Assert.Throws<FileNotFoundException>(() => DskFilePropertyService.Apply(document, entry, DskFileProperties.From(entry)));
        Assert.Equal(before, document.Serialize());
    }

    [Fact]
    public void UnsupportedFieldsAndFilesystemAreNotEditable()
    {
        var cpm = DskDocumentFactory.CreateCpm(false); cpm.FileSystem.Insert("TEST", [1]); cpm.MarkModified();
        var entry = cpm.FileSystem.ReadDirectory()[0]; var before = cpm.Serialize();
        Assert.Throws<InvalidDataException>(() => DskFilePropertyService.Apply(cpm, entry, DskFileProperties.From(entry) with { Load = 0x1234 }));
        Assert.Equal(before, cpm.Serialize());
        Assert.Equal(DskFilePropertyKind.None, DskCapabilityService.GetFilePropertyKind(DskDocumentFactory.CreateFsmz()));
        Assert.Equal(DskFilePropertyKind.None, DskCapabilityService.GetFilePropertyKind(null));
        var mrs = DskDocumentFactory.CreateMrs(); mrs.FileSystem.Insert("TEST", [1]); mrs.MarkModified();
        entry = mrs.FileSystem.ReadDirectory()[0]; before = mrs.Serialize();
        Assert.Throws<InvalidDataException>(() => DskFilePropertyService.Apply(mrs, entry, DskFileProperties.From(entry) with { User = 3 }));
        Assert.Equal(before, mrs.Serialize());
    }

    [Fact]
    public void BootGeometryMismatchReportShowsTargetAndSource()
    {
        var check = DskCapabilityService.CanInstallBootSystem(DskDocumentFactory.CreateCpm(true), DskDocumentFactory.CreateCpm(false));
        Assert.False(check.IsCompatible); Assert.Contains("physical track 0", check.Reason);
        Assert.Contains("Target:", check.Reason); Assert.Contains("Source:", check.Reason);
        Assert.Contains("18 sectors", check.Reason); Assert.Contains("9 sectors", check.Reason);
    }

    internal static void ExerciseDialogOnCurrentStaThread()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(DskFilePropertiesDialog);
        var entry = new DskFileEntry { Key = "0", Name = "TEST", User = 2, LoadAddress = 0x1234, ExecuteAddress = 0x5678 };
        object Dialog(DskFilePropertyKind kind) => type.GetConstructors(flags).Single().Invoke([null, kind, entry]);
        object Field(object dialog, string name) => type.GetField(name, flags)!.GetValue(dialog)!;
        void Set(object dialog, string name, object value, string property = "Text") => Field(dialog, name).GetType().GetProperty(property)!.SetValue(Field(dialog, name), value);
        var cpm = Dialog(DskFilePropertyKind.Cpm);
        Set(cpm, "user", "7"); Set(cpm, "readOnly", true, "IsChecked");
        var values = ((DskFilePropertiesDialog)cpm).ReadValues(); Assert.Equal(7, values.User); Assert.True(values.ReadOnly);
        Assert.Equal(2, entry.User); Assert.False(entry.ReadOnly); // Editing and cancellation never bind-write the entry.
        Set(cpm, "user", "16"); Assert.Throws<ArgumentException>(() => ((DskFilePropertiesDialog)cpm).ReadValues());
        type.GetMethod("Close")!.Invoke(cpm, null);
        var mrs = Dialog(DskFilePropertyKind.Mrs); Set(mrs, "load", "0xABCD"); Set(mrs, "execute", "5678");
        values = ((DskFilePropertiesDialog)mrs).ReadValues(); Assert.Equal(0xABCD, values.Load); Assert.Equal(0x5678, values.Execute);
        Set(mrs, "load", "10000"); Assert.Throws<ArgumentException>(() => ((DskFilePropertiesDialog)mrs).ReadValues());
        Assert.Equal(0x1234, entry.LoadAddress); type.GetMethod("Close")!.Invoke(mrs, null);
    }
}
