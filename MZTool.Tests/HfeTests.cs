using System.Buffers.Binary;

namespace MZTools.Tests;

public class HfeTests
{
    [Fact]
    public void V2IndependentInstructionsPreserveCellsWeakIndexAndControls()
    {
        var bytes=Vector(false,[0x12,0,1,72,0xAA,0,4,1,0,0,3,0,0,0,2,0xCC,0,0,7,2,0,0,4,0,0,0,5,0,0]); bytes[8]=1;
        var image=HfeImage.Parse(bytes); var track=image.Tracks.Single();
        Assert.Equal(HfeVersion.V2,image.Version); Assert.Equal("HFEv2",image.Identify(bytes).Variant);
        Assert.Equal(24,track.BitCellCount); Assert.Equal(new long[] {8},track.IndexCells);
        Assert.Equal(8,track.WeakBitMask.Sum(b=>System.Numerics.BitOperations.PopCount((uint)b)));
        Assert.Equal(4,track.V2Controls.Count); Assert.Equal((byte)72,track.V2Controls[0].Value);
        Assert.Equal(bytes,image.Serialize(HfeVersion.V2));
        HfeImage.VerifyPhysical(image,HfeImage.Parse(image.Serialize(HfeVersion.V2,false)));
        Assert.Throws<InvalidDataException>(()=>image.Serialize(HfeVersion.V3));
        Assert.Throws<InvalidDataException>(()=>image.Serialize(HfeVersion.V1));
    }
    [Fact]
    public void V2BreakBufferSkipsPaddingWithoutCreatingCells()
    {
        var stream=Enumerable.Repeat((byte)0xFF,260).ToArray();
        new byte[] {0x12,0,6,0xAA,0xAA}.CopyTo(stream,0);
        new byte[] {0,3,0x34,0}.CopyTo(stream,256);
        var bytes=Vector(false,stream);bytes[8]=1;
        var image=HfeImage.Parse(bytes);Assert.Equal(16,image.Tracks.Single().BitCellCount);
        HfeImage.VerifyPhysical(image,HfeImage.Parse(image.Serialize(HfeVersion.V2,false)));
        HfeImage.VerifyPhysical(image,HfeImage.Parse(image.Serialize(HfeVersion.V3)));
    }
    [Fact]
    public void V2RewriteKeepsInstructionsInsideSideBuffers()
    {
        var stream=Enumerable.Repeat((byte)0x12,248)
            .Concat(new byte[] {0,2,0xAA,0,0,1,72,0})
            .Concat(Enumerable.Repeat((byte)0x34,251))
            .Concat(new byte[] {0,3,0,0,0x56,0x78}).ToArray();
        var bytes=Vector(false,stream);bytes[8]=1;var image=HfeImage.Parse(bytes);
        var rewritten=image.Serialize(HfeVersion.V2,false);var decoded=HfeImage.Parse(rewritten);
        HfeImage.VerifyPhysical(image,decoded);
        int length=BinaryPrimitives.ReadUInt16LittleEndian(rewritten.AsSpan(514))/2;
        for(int i=0;i<length;i++) {
            byte value=PackedBitCells.Reverse(rewritten[1024+i/256*512+i%256]);
            if(value!=0) continue;
            Assert.True(i%256<=252,"Four-byte v2 instruction crosses the hardware buffer");
            byte op=PackedBitCells.Reverse(rewritten[1024+(i+1)/256*512+(i+1)%256]);
            i+=3;if(op==6) i=((i+1+255)/256)*256-1;
        }
    }
    [Theory]
    [InlineData(new byte[] {0})]
    [InlineData(new byte[] {0,3})]
    [InlineData(new byte[] {0,8,0,0})]
    public void InvalidV2InstructionsAreRejected(byte[] stream)
    {
        var bytes=Vector(false,stream);bytes[8]=1;
        Assert.Throws<InvalidDataException>(()=>HfeImage.Parse(bytes));
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AllVersionsExportAndDecodeTwoSidedDisk(int versionNumber)
    {
        var version=(HfeVersion)versionNumber;
        var document=DskDocumentFactory.CreateFsmz(tracks:4);
        var before=document.Serialize(); var generated=HfeImage.FromDsk(document);
        var image=HfeImage.Parse(generated.Serialize(version));
        Assert.Equal(version,image.Version); HfeImage.VerifyPhysical(generated,image);
        Assert.Equal(before,document.Serialize());
        Assert.Contains("HFEv2",BatchMediaPolicy.Targets);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownEncodingOpensPhysicalViewAndOnlyAllowsOriginalVariantCopy(bool v3)
    {
        byte[] bytes = Vector(v3, [0x12, 0x34, 0x56, 0x78]); bytes[11] = 4;
        var image = HfeImage.Parse(bytes);
        Assert.Equal(PhysicalEncoding.Unknown, image.Tracks[0].Encoding);
        Assert.Equal((byte?)4, image.Tracks[0].ContainerEncoding);
        Assert.Empty(image.Tracks[0].Sectors);
        Assert.Equal(32, image.Tracks[0].BitCellCount);
        Assert.Equal(bytes, image.Serialize(v3));
        Assert.Throws<InvalidDataException>(() => image.Serialize(!v3));
        Assert.Throws<InvalidDataException>(() => image.Serialize(v3, false));
        Assert.False(HfeSemanticInspectionService.Inspect(image).Available);
        Assert.Equal(bytes, image.Serialize(v3));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void KnownSharpFilesystemInspectionIsReadOnly(int kind)
    {
        var document = kind switch
        {
            0 => DskDocumentFactory.CreateFsmz(tracks: 4),
            1 => DskDocumentFactory.CreateCpm(false, tracks: 6),
            2 => DskDocumentFactory.CreateMrs(),
            _ => DskDocumentFactory.CreatePersonalCpm80(false)
        };
        document.FileSystem.Insert("TEST", [1, 2, 3]); document.MarkModified();
        var image = HfeImage.Parse(HfeImage.FromDsk(document).Serialize(true));
        var original = image.Serialize(true);
        var inspection = HfeSemanticInspectionService.Inspect(image);
        Assert.True(inspection.Available, inspection.Report);
        Assert.Equal(1, inspection.FileCount);
        Assert.Contains("TEST", inspection.Report);
        Assert.Contains("read-only", inspection.Report);
        Assert.Contains("synthetic", inspection.Report);
        Assert.Equal(original, image.Serialize(true));
        image.Tracks[0].Sectors = image.Tracks[0].Sectors.Select((s, i) => i == 0 ? s with { HeaderCrcValid = false } : s).ToArray();
        Assert.False(HfeSemanticInspectionService.Inspect(image).Available);
        Assert.Equal(original, image.Serialize(true));
    }

    [Fact]
    public void NonSectorTrackAndUnknownBitrateRemainInspectable()
    {
        byte[] bytes = Vector(false, [0, 0, 0, 0]); bytes[12] = bytes[13] = bytes[14] = bytes[15] = 0;
        var image = HfeImage.Parse(bytes);
        Assert.Null(image.Tracks[0].BitRate); Assert.Null(image.Tracks[0].Rpm);
        Assert.Empty(image.Tracks[0].Sectors);
        Assert.Equal(bytes, image.Serialize(false));
        Assert.False(HfeSemanticInspectionService.Inspect(image).Available);
    }

    [Fact]
    public void OverlappingTrackOffsetsAreRejected()
    {
        byte[] bytes = Vector(false, [1, 2]); bytes[9] = 2;
        bytes.AsSpan(512, 4).CopyTo(bytes.AsSpan(516));
        Assert.Throws<InvalidDataException>(() => HfeImage.Parse(bytes));
    }

    [Fact]
    public void RegistryCapabilitiesMatchPhysicalPreservationSupport()
    {
        var dsk = MediaFormats.All.Single(f => f.Id == "extended-dsk");
        var hfe = MediaFormats.All.Single(f => f.Id == "hfe");
        Assert.False(dsk.Capabilities.HasFlag(MediaCapabilities.PreserveWeakBits));
        Assert.False(dsk.Capabilities.HasFlag(MediaCapabilities.PreserveTiming));
        Assert.True(hfe.Capabilities.HasFlag(MediaCapabilities.PreserveWeakBits));
        Assert.True(hfe.Capabilities.HasFlag(MediaCapabilities.PreserveTiming));
        Assert.True(hfe.Identify(Vector(true, [0xF4])).Matches);
        Assert.True(dsk.Identify(DskDocumentFactory.CreateFsmz(tracks: 4).Serialize()).Matches);
    }

    // Independently assembled format vectors (not produced by the MZTools writer).
    private static byte[] Vector(bool v3, byte[] canonicalStream)
    {
        var bytes = Enumerable.Repeat((byte)0xFF, 1024 + (canonicalStream.Length * 2 + 511) / 512 * 512).ToArray();
        (v3 ? "HXCHFEV3"u8 : "HXCPICFE"u8).CopyTo(bytes);
        bytes[8] = 0; bytes[9] = 1; bytes[10] = 1; bytes[11] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 250);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 300);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(512), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(514), (ushort)(canonicalStream.Length * 2));
        for (int i = 0; i < canonicalStream.Length; i++) bytes[1024 + (i / 256) * 512 + i % 256] = PackedBitCells.Reverse(canonicalStream[i]);
        return bytes;
    }

    [Fact]
    public void KnownLegacyVectorReadsLsbPhysicalCellsAndRoundtripsExactly()
    {
        var bytes = Vector(false, [0x44, 0x89, 0x12, 0x34]); var image = HfeImage.Parse(bytes);
        Assert.False(image.IsV3); Assert.Equal(32, image.Tracks[0].BitCellCount);
        Assert.Equal(250000, image.Tracks[0].BitRate); Assert.Equal(300, image.Tracks[0].Rpm);
        Assert.Equal(bytes, image.Serialize(false));
        HfeImage.VerifyPhysical(image, HfeImage.Parse(image.Serialize(true)));
    }

    [Fact]
    public void KnownV3VectorPreservesSkipWeakIndexAndBitrate()
    {
        var bytes = Vector(true, [0xF1, 0xF2, 72, 0x12, 0xF3, 4, 0x0A, 0xF4, 0xF0]); var image = HfeImage.Parse(bytes); var t = image.Tracks[0];
        Assert.True(image.IsV3); Assert.Equal(20, t.BitCellCount); Assert.Equal(new long[] { 0 }, t.IndexCells);
        Assert.Equal(72, t.Timing.Single().Divisor);
        Assert.Equal(8, Enumerable.Range(0, 20).Count(i => PackedBitCells.Get(t.WeakBitMask, i)));
        Assert.Equal(bytes, image.Serialize(true));
        HfeImage.VerifyPhysical(image, HfeImage.Parse(image.Serialize(true, false)));
        Assert.Throws<InvalidDataException>(() => image.Serialize(false));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DskToHfePreservesOrderDataAndDoesNotFabricateBadCrc(bool v3)
    {
        var document = DskDocumentFactory.CreateRaw(1, 2, 8, 512, 1, 0x4E, 0xE5, "test", DskDocumentFactory.RawSectorOrder.PersonalCpm80);
        document.Image.Tracks[0]!.Sectors[0].FdcStatus1 = 0x20;
        document.Image.Tracks[0]!.Sectors[0].Data[0] = 0xFA;
        document.MarkModified(); var original = document.Serialize();
        var preview = MediaConversionService.DskToHfe(document, v3);
        Assert.Equal(original, document.Serialize());
        var physical = HfeImage.Parse(preview.Output);
        for (int t = 0; t < 2; t++)
        {
            Assert.Equal(document.Image.Tracks[t]!.Sectors.Select(s => s.SectorId), physical.Tracks[t].Sectors.Select(s => s.R));
            Assert.All(physical.Tracks[t].Sectors, s => Assert.True(s.HeaderCrcValid && s.DataCrcValid));
            for (int p = 0; p < 8; p++) Assert.Equal(document.Image.Tracks[t]!.Sectors[p].Data, physical.Tracks[t].Sectors[p].Data);
        }
        var dsk = MediaConversionService.HfeToDsk(physical);
        Assert.True(dsk.IsLossy); Assert.Contains("weak bits", dsk.Report);
        Assert.Equal(0, DskImage.Parse(dsk.Output).Tracks[0]!.Sectors[0].FdcStatus1);
        if (!v3) HfeImage.VerifyPhysical(physical, HfeImage.Parse(physical.Serialize(true)));
    }

    [Theory]
    [InlineData(0xF5)] [InlineData(0xFF)]
    public void UnknownV3OpcodesAreRejected(byte opcode) => Assert.Throws<InvalidDataException>(() => HfeImage.Parse(Vector(true, [opcode])));

    [Fact]
    public void TruncatedTableAndTrackAreRejected()
    {
        byte[] bytes = Vector(false, [1, 2]);
        Assert.Throws<InvalidDataException>(() => HfeImage.Parse(bytes[..1200]));
        bytes[18] = 0xFF; bytes[19] = 0xFF;
        Assert.Throws<InvalidDataException>(() => HfeImage.Parse(bytes));
    }

    [Fact]
    public void IndependentFmTrackDecodesMarksAndReportsActualBadCrc()
    {
        Assert.Equal(0x29B1, FloppyTrackCodec.Crc("123456789"u8));
        var words = new List<ushort>();
        void Byte(byte value, byte clocks = 0xFF)
        {
            ushort raw = 0;
            for (int bit = 7; bit >= 0; bit--) raw = (ushort)((raw << 2) | (((clocks >> bit) & 1) << 1) | ((value >> bit) & 1));
            words.Add(raw);
        }
        void Crc(byte[] data) { ushort crc = FloppyTrackCodec.Crc(data); Byte((byte)(crc >> 8)); Byte((byte)crc); }
        for (int i = 0; i < 16; i++) Byte(0xFF);
        Byte(0xFE, 0xC7); byte[] header = [0xFE, 0, 0, 7, 0]; foreach (byte b in header.Skip(1)) Byte(b); Crc(header);
        for (int i = 0; i < 16; i++) Byte(0xFF);
        Byte(0xFB, 0xC7); byte[] payload = Enumerable.Repeat((byte)0x42, 128).ToArray(); foreach (byte b in payload) Byte(b);
        Crc(new byte[] { 0xFB }.Concat(payload).ToArray()); for (int i = 0; i < 32; i++) Byte(0xFF);
        byte[] stream = words.SelectMany(w => new byte[] { (byte)(w >> 8), (byte)w }).ToArray();
        byte[] bytes = Vector(false, stream); bytes[11] = 2;
        var image = HfeImage.Parse(bytes); var sector = Assert.Single(image.Tracks[0].Sectors);
        Assert.Equal(7, sector.R); Assert.Equal(payload, sector.Data); Assert.True(sector.HeaderCrcValid); Assert.True(sector.DataCrcValid);
        // Change one physical data bit rather than invent a status flag.
        int cell = (int)sector.DataCell + 1; int byteIndex = cell / 8;
        bytes[1024 + (byteIndex / 256) * 512 + byteIndex % 256] ^= (byte)(1 << (cell % 8));
        var bad = Assert.Single(HfeImage.Parse(bytes).Tracks[0].Sectors); Assert.False(bad.DataCrcValid); Assert.True(bad.HeaderCrcValid);
        var converted = DskImage.Parse(MediaConversionService.HfeToDsk(HfeImage.Parse(bytes)).Output);
        Assert.Equal(0x20, converted.Tracks[0]!.Sectors[0].FdcStatus1); Assert.Equal(0x20, converted.Tracks[0]!.Sectors[0].FdcStatus2);
    }
}



public sealed class HfeNewImageTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CustomGeometryIdsAndFillerArePreserved(int variant)
    {
        var options=new DskNewOptions(DskNewFormat.CustomRaw,3,1,3,1024,0xA7,
            DskDocumentFactory.RawSectorOrder.Custom,new[] {9,7,3},(HfeVersion)variant);
        var created=DiskFormatService.CreateNew(options);
        var physical=HfeImage.Parse(created.SerializeContainer());
        AssertSectorsEqual(DiskFormatService.Create(options).Image,physical);
        Assert.All(physical.Tracks,track=> {
            Assert.Equal(new byte[] {9,7,3},track.Sectors.Select(s=>s.R));
            Assert.All(track.Sectors,sector=>Assert.All(sector.Data,value=>Assert.Equal((byte)0xA7,value)));
        });
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AllFilesystemPresetsKeepTheirSectorsInNewHfe(int variant)
    {
        foreach(var format in new[] {DskNewFormat.MzBasic,DskNewFormat.IplDisk,DskNewFormat.PersonalCpm,DskNewFormat.Sds400,DskNewFormat.LecCpmDd,DskNewFormat.LecCpmHd,DskNewFormat.Mrs,DskNewFormat.Lemmings,DskNewFormat.CustomRaw}) {
            var options=new DskNewOptions(format,format==DskNewFormat.Lemmings?80:40,2,HfeVersion:(HfeVersion)variant);
            var expected=DiskFormatService.Create(options);
            var actual=DiskFormatService.CreateNew(options);
            Assert.Null(actual.FilePath);Assert.True(actual.IsModified);
            var reopened=HfeImage.Parse(actual.SerializeContainer());Assert.Equal((HfeVersion)variant,reopened.Version);
            AssertSectorsEqual(expected.Image,reopened);
        }
    }

    [Theory]
    [InlineData(1,false)] [InlineData(2,false)] [InlineData(3,false)]
    [InlineData(1,true)] [InlineData(2,true)] [InlineData(3,true)]
    public void IplHfeSavesReopensAndRebuildsWithoutLosingVariant(int variant,bool multi)
    {
        var record=TapeTestData.ReadRecord(TapeTestData.CreateMzf(Enumerable.Range(0,700).Select(i=>(byte)i).ToArray()));
        byte[] Build(string name)=>multi?Mz800MultiGameIplDskWriter.Build([
            new(record,name,new MzfCompressionOptions(MzfCompressionAlgorithm.None),record.Body.MzfBody.Length),
            new(record,"SECOND",new MzfCompressionOptions(MzfCompressionAlgorithm.None),record.Body.MzfBody.Length)]).Image:Mz800IplDskWriter.Build(record,name);
        byte[] initial=Build("FIRST");var expected=DskDocument.Open(initial);
        var document=DiskFormatService.SetNewContainer(DskDocument.Open(initial),(HfeVersion)variant);
        AssertSectorsEqual(expected.Image,HfeImage.Parse(document.SerializeContainer()));
        string directory=TapeTestData.CreateTempDirectory();
        try {
            string path=Path.Combine(directory,"ipl.hfe");document.Save(path);
            var opened=DskDocument.Open(path);
            Assert.Equal(multi?DskFileSystemType.MultiIpl:DskFileSystemType.SingleIpl,opened.FileSystem.Type);
            Assert.Equal(multi?2:1,opened.FileSystem.ReadDirectory().Count);
            foreach(var entry in opened.FileSystem.ReadDirectory()) Assert.Equal(record.Body.MzfBody,opened.FileSystem.Extract(entry));
            byte[] rebuilt=Build("RENAMED");opened.ReplaceContents(rebuilt);opened.Save();
            var physical=HfeImage.Parse(File.ReadAllBytes(path));Assert.Equal((HfeVersion)variant,physical.Version);
            AssertSectorsEqual(DskDocument.Open(rebuilt).Image,physical);
            Assert.Contains(DskDocument.Open(path).FileSystem.ReadDirectory(),entry=>entry.Name=="RENAMED");
            if(multi) {
                // Native IPL stores sector payload inverted; the ROM displays decoded bytes.
                byte[] content=physical.Tracks.SelectMany(t=>t.Sectors).SelectMany(s=>s.Data).Select(value=>(byte)~value).ToArray();
                Assert.True(content.AsSpan().IndexOf(SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes("MZTools Multiboot"))>=0);
                Assert.True(content.AsSpan().IndexOf(SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes("MZTools Multi Game"))<0);
            }
        } finally {Directory.Delete(directory,true);}
    }

    private static void AssertSectorsEqual(DskImage expected,HfeImage actual)
    {
        Assert.Equal(expected.TrackCount,actual.Cylinders);Assert.Equal(expected.SideCount,actual.Sides);
        foreach(var track in actual.Tracks) {
            var sectors=expected.GetTrack(track.Cylinder,track.Side).Sectors;
            Assert.Equal(sectors.Count,track.Sectors.Count);
            for(int index=0;index<sectors.Count;index++) {
                var sector=sectors[index];var decoded=track.Sectors[index];
                Assert.Equal(sector.SectorId,decoded.R);Assert.Equal(sector.Data,decoded.Data);
                Assert.True(decoded.HeaderCrcValid && decoded.DataCrcValid);
            }
        }
    }
}
