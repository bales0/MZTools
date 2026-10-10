namespace MZTools.Tests;

public sealed class WorkspaceDragTransferTests
{
    private static TapeRecord Record(string name = "TEST") => TapeTestData.ReadRecord(TapeTestData.CreateMzf([1, 2, 3], name: name));
    private static WorkspaceDragPacket Wire(WorkspaceDragPacket packet) => WorkspaceDragTransfer.Decode(WorkspaceDragTransfer.Encode(packet));
    private static DskDocument Disk(string kind) => kind switch
    {
        "fsmz" => DskDocumentFactory.CreateFsmz(), "mrs" => DskDocumentFactory.CreateMrs(),
        _ => DskDocumentFactory.CreateCpm(false)
    };

    [Theory]
    [InlineData("fsmz", "fsmz")] [InlineData("fsmz", "cpm")] [InlineData("fsmz", "mrs")]
    [InlineData("cpm", "fsmz")] [InlineData("cpm", "cpm")] [InlineData("cpm", "mrs")]
    [InlineData("mrs", "fsmz")] [InlineData("mrs", "cpm")] [InlineData("mrs", "mrs")]
    public void DiskSelectionSurvivesWireWithoutSourceReferences(string from, string to)
    {
        var source = Disk(from); var target = Disk(to);
        source.FileSystem.Insert("A.BIN", [1, 2, 3], 1, 0x1200, 0x1200);
        source.FileSystem.Insert("B.BIN", [4, 5, 6]); source.MarkModified();
        var entry = source.FileSystem.ReadDirectory().First();
        var packet = Wire(WorkspaceDragTransfer.Capture(source, [entry]));
        byte[] before = source.Serialize(), targetBefore = target.Serialize();
        var detached = WorkspaceDragTransfer.OpenDisk(packet);
        var preview = CrossDiskTransferService.Preview(detached, target, packet.Keys.Select(k => new DiskCopyRequest(k)).ToArray(), DiskCollisionPolicy.Cancel);
        Assert.Equal(before, source.Serialize()); Assert.Equal(targetBefore, target.Serialize());
        preview.Operation.Apply(target);
        Assert.Single(target.FileSystem.ReadDirectory());
        Assert.Equal(source.FileSystem.Extract(entry), target.FileSystem.Extract(target.FileSystem.ReadDirectory()[0]).Take(source.FileSystem.Extract(entry).Length));
        Assert.Equal(before, source.Serialize());
    }

    [Fact]
    public void AttachedLayoutSurvivesWireAndCopy()
    {
        var image = DskImage.CreateUniform(10, 1, 8, 512, 1, 0x4E, 0xE5, "custom");
        var dpb = new CpmDpb(32, 3, 7, 0, 35, 31, 0x80, 0, 8, 1, 1024, "Custom drag layout");
        CpmFileSystem.Format(image, dpb);
        var source = DskDocument.Open(image.Serialize()); source.AttachCpmLayout(dpb);
        source.FileSystem.Insert("A.COM", [0xC9], user: 3); source.MarkModified();
        var packet = Wire(WorkspaceDragTransfer.Capture(source, source.FileSystem.ReadDirectory()));
        var detached = WorkspaceDragTransfer.OpenDisk(packet);
        Assert.Equal(dpb, detached.AttachedDpb);
        var target = Disk("cpm");
        CrossDiskTransferService.Preview(detached, target, [new(packet.Keys[0])], DiskCollisionPolicy.Cancel).Operation.Apply(target);
        Assert.Equal(3, target.FileSystem.ReadDirectory().Single().User);
    }

    [Fact]
    public void TapeCopyPreservesMetadataAndSelectionOrderAndDoesNotMutateSource()
    {
        var source = Record("FIRST"); source.Profile = TapeProfile.Tc1_3; source.MetadataOrigin = MetadataOrigin.LoadedFromM2i;
        var body = source.Body; body.TrailingData = [0xFA, 0xFB]; source.Body = body;
        source.RawHeader[30] = 0xAC;
        var packet = Wire(WorkspaceDragTransfer.Capture(new[] { source, Record("SECOND") }));
        var target = new TapeDocument { Format = TapeDocumentFormat.Mzt }; target.Records.Add(Record("KEEP"));
        var preview = new TapeCopyPreview(target, packet, 0);
        Assert.Single(target.Records); Assert.False(target.IsModified); // cancel leaves target intact
        preview.Apply(target);
        Assert.Equal(3, target.Records.Count); Assert.True(target.IsModified);
        Assert.Equal(source.GetSerializedHeader(), target.Records[0].GetSerializedHeader());
        Assert.Equal(source.Body.TrailingData, target.Records[0].Body.TrailingData);
        Assert.Equal(source.Profile, target.Records[0].Profile); Assert.Equal(source.MetadataOrigin, target.Records[0].MetadataOrigin);
        Assert.NotSame(source, target.Records[0]); target.Records[0].Body.MzfBody[0] = 99;
        Assert.Equal(1, source.Body.MzfBody[0]);
        Assert.Contains("SECOND", SharpMzEncoding.ConvertMzfNameToASCIIString(target.Records[1].Header.MzfFname));
    }

    [Fact]
    public void TapePreviewRejectsStaleTargetAndQuickDiskOverflow()
    {
        var packet = Wire(WorkspaceDragTransfer.Capture(new[] { Record() }));
        var target = new TapeDocument { Format = TapeDocumentFormat.Mzt }; target.Records.Add(Record());
        var preview = new TapeCopyPreview(target, packet, 0);
        target.Records[0].Profile = TapeProfile.Ultra;
        Assert.Throws<InvalidOperationException>(() => preview.Apply(target)); Assert.Single(target.Records);
        target.Format = TapeDocumentFormat.Mzq;
        while (target.Records.Count < QuickDiskLimits.StandardDirectoryEntries) target.Records.Add(Record());
        Assert.Throws<InvalidDataException>(() => new TapeCopyPreview(target, packet, 0));
        Assert.Equal(QuickDiskLimits.StandardDirectoryEntries, target.Records.Count);
    }

    [Fact]
    public void EmptyTargetBecomesUnsavedDocumentAfterApplyOnly()
    {
        var target = new TapeDocument();
        var preview = new TapeCopyPreview(target, Wire(WorkspaceDragTransfer.Capture(new[] { Record() })), 0);
        Assert.Equal(TapeDocumentFormat.None, target.Format); preview.Apply(target);
        Assert.Equal(TapeDocumentFormat.Mzf, target.Format); Assert.Null(target.FilePath); Assert.True(target.IsModified);
    }

    [Fact]
    public void InvalidVersionSelectionAndRecordAreRejected()
    {
        var packet = WorkspaceDragTransfer.Capture(new[] { Record() });
        Assert.Throws<InvalidDataException>(() => Wire(packet with { Version = 2 }));
        Assert.Throws<InvalidDataException>(() => Wire(packet with { Records = [] }));
        Assert.Throws<InvalidDataException>(() => WorkspaceDragTransfer.OpenRecords(packet with { Records = [packet.Records[0] with { Payload = [1] }] }));
        Assert.Throws<InvalidDataException>(() => WorkspaceDragTransfer.OpenRecords(packet with { Records = [packet.Records[0] with { Payload = [1, 2, 3, 4] }] }));
        var disk = Disk("cpm"); disk.FileSystem.Insert("A.COM", [0xC9]); disk.MarkModified();
        var diskPacket = WorkspaceDragTransfer.Capture(disk, disk.FileSystem.ReadDirectory());
        Assert.Throws<InvalidDataException>(() => Wire(diskPacket with { Keys = [diskPacket.Keys[0], diskPacket.Keys[0]] }));
    }
}
