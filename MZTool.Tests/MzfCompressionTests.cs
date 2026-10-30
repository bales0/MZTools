using System.Security.Cryptography;

namespace MZTools.Tests;

public class MzfCompressionTests
{
    [Theory]
    [InlineData(2, 0, false, false)]
    [InlineData(2, 0, true, false)]
    [InlineData(2, 1, false, false)]
    [InlineData(2, 1, true, false)]
    [InlineData(3, 0, false, false)]
    [InlineData(3, 1, false, false)]
    [InlineData(3, 0, false, true)]
    [InlineData(3, 1, false, true)]
    public void Decompression_RestoresKnownCompressionVariants(
        int algorithmValue,
        int directionValue,
        bool quick,
        bool embedded)
    {
        var algorithm = (MzfCompressionAlgorithm)algorithmValue;
        var direction = (CompressionDirection)directionValue;
        TapeRecord source = CreateReferenceRecord();
        var options = new MzfCompressionOptions(algorithm, direction, quick, embedded);
        TapeRecord packed = MzfCompressionService.Compress(
            source,
            options,
            CompressionTarget.MzfTape).Record;

        MzfDecompressionResult result = MzfDecompressionService.Decompress(packed);

        Assert.Equal(source.Body.MzfBody, result.Record.Body.MzfBody);
        Assert.Equal(source.Header.MzfSize, result.Record.Header.MzfSize);
        Assert.Equal(source.Header.MzfStart, result.Record.Header.MzfStart);
        Assert.Equal(source.Header.MzfExec, result.Record.Header.MzfExec);
        Assert.Equal("None / unknown", MzfLoaderBuilder.DetectCompression(result.Record));
        Assert.Equal(!embedded, result.HeaderDescriptionFullyRestored);
        if (!embedded)
        {
            Assert.Equal(source.GetSerializedHeader(), result.Record.GetSerializedHeader());
        }
        else
        {
            Assert.All(result.Record.DescriptionRaw.AsSpan(0, result.Compression.DecoderLength).ToArray(),
                value => Assert.Equal(0, value));
        }
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    public void Decompression_RejectsPartialCompressionThatRequiresExternalData(
        int algorithmValue,
        int directionValue)
    {
        TapeRecord source = CreateReferenceRecord();
        var algorithm = (MzfCompressionAlgorithm)algorithmValue;
        var direction = (CompressionDirection)directionValue;
        const int skip = 17;
        TapeRecord packed = MzfCompressionService.Compress(
            source,
            new MzfCompressionOptions(algorithm, direction, SkipBytes: skip),
            CompressionTarget.MzfTape).Record;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => MzfDecompressionService.Decompress(packed));
        Assert.Contains("partial/skip", exception.Message);
    }

    [Fact]
    public void Decompression_RejectsTruncatedPayloadWithoutChangingSource()
    {
        TapeRecord source = CreateReferenceRecord();
        TapeRecord packed = MzfCompressionService.Compress(
            source,
            new MzfCompressionOptions(MzfCompressionAlgorithm.Zx7),
            CompressionTarget.MzfTape).Record;
        MZQFileBody body = packed.Body;
        body.MzfBody = body.MzfBody[..^1];
        body.DataSize--;
        packed.Body = body;

        Assert.Throws<InvalidDataException>(() => MzfDecompressionService.Decompress(packed));
        Assert.Equal(source.Body.MzfBody.Length, source.Body.DataSize);
    }

    [Fact]
    public async Task ExportPolicy_PreservesKnownCompressedRecordAndLocksRecompression()
    {
        TapeRecord source = CreateReferenceRecord();
        TapeRecord packed = MzfCompressionService.Compress(
            source,
            new MzfCompressionOptions(MzfCompressionAlgorithm.Zx0),
            CompressionTarget.MztTape).Record;
        byte[] packedBody = packed.Body.MzfBody.ToArray();

        Assert.True(SaveOptionsDialog.IsCompressionLocked(packed, out string detected));
        Assert.Contains("ZX0", detected);

        MzfCompressionResult exported = await SaveOptionsDialog.PrepareCompressionForExportAsync(
            packed,
            new MzfCompressionOptions(MzfCompressionAlgorithm.Zx7),
            CompressionTarget.MztTape);

        Assert.NotSame(packed, exported.Record);
        Assert.Equal(packedBody, exported.Record.Body.MzfBody);
        Assert.Equal(detected, MzfLoaderBuilder.DetectCompression(exported.Record));
        Assert.Equal(MzfCompressionAlgorithm.None, exported.AppliedOptions.Algorithm);
    }

    [Fact]
    public async Task ExportPolicy_DecompressesKnownRecordWhenRequested()
    {
        TapeRecord source = CreateReferenceRecord();
        TapeRecord packed = MzfCompressionService.Compress(
            source,
            new MzfCompressionOptions(MzfCompressionAlgorithm.Zx7),
            CompressionTarget.MztTape).Record;

        MzfCompressionResult exported = await SaveOptionsDialog.PrepareCompressionForExportAsync(
            packed,
            new MzfCompressionOptions(MzfCompressionAlgorithm.Zx0),
            CompressionTarget.MztTape,
            decompressKnownCompression: true);

        Assert.Equal(source.Body.MzfBody, exported.Record.Body.MzfBody);
        Assert.Equal(source.Header.MzfStart, exported.Record.Header.MzfStart);
        Assert.Equal(source.Header.MzfExec, exported.Record.Header.MzfExec);
        Assert.Equal("None / unknown", MzfLoaderBuilder.DetectCompression(exported.Record));
        Assert.Equal("ZX7 (forward)", MzfLoaderBuilder.DetectCompression(packed));
    }

    public static TheoryData<string, string, bool, bool, bool, int, int, int, int, string> ReferenceVectors =>
        new()
        {
            {
                "ZX0 forward",
                "ZX0", false, false, false, 0, 503, 0x3279, 0x3279,
                "5DFE316AA73BE8DB63BD6AE7707223B43F63A6A4516184E9ACE3D5888EDFF988"
            },
            {
                "ZX0 backward",
                "ZX0", true, false, false, 0, 503, 0x2FFE, 0x3185,
                "21BB1819395D5A606AC6AE26535DE3B11D74E7BCE5510623F9E4942C87F96F81"
            },
            {
                "ZX0 quick",
                "ZX0", false, true, false, 0, 503, 0x3279, 0x3279,
                "5DFE316AA73BE8DB63BD6AE7707223B43F63A6A4516184E9ACE3D5888EDFF988"
            },
            {
                "ZX0 quick backward",
                "ZX0", true, true, false, 0, 503, 0x2FFE, 0x3185,
                "21BB1819395D5A606AC6AE26535DE3B11D74E7BCE5510623F9E4942C87F96F81"
            },
            {
                "ZX0 skip",
                "ZX0", false, false, false, 17, 486, 0x328A, 0x328A,
                "68C38FD8EA25744EAD8CA8684A6F58D2B4D1B51D007106ADF3D53CCAE3265A79"
            },
            {
                "ZX0 quick backward skip",
                "ZX0", true, true, false, 17, 486, 0x2FFE, 0x3174,
                "466B8DB01FC39E188EE01BDFFD523B5426EEB08D0D31AA0677DE848EDEB9A96A"
            },
            {
                "ZX7 forward",
                "ZX7", false, false, false, 0, 532, 0x325C, 0x325C,
                "824F77C445F4545421364CA25A7FFCC7AC50AB64DC4214918C0D136971B6457A"
            },
            {
                "ZX7 backward",
                "ZX7", true, false, false, 0, 536, 0x2FFE, 0x31A6,
                "936B6F7998E60B884575C91D6922043D7B317ED7DD2B40B50467EC96E3B050E5"
            },
            {
                "ZX7 embedded",
                "ZX7", false, false, true, 0, 441, 0x325C, 0x325C,
                "529B2A7EC50240FF8A78176C214019E3D8FFAB06B58D8DDF7FD6C8AAFBA6D78C"
            },
            {
                "ZX7 backward embedded",
                "ZX7", true, false, true, 0, 445, 0x2FFE, 0x31A6,
                "530022F930EFB2E029F1D1DBD6ABCFE31C2E22C78FA2CD1EF4F1EC3B4A9B4BCF"
            },
            {
                "ZX7 skip",
                "ZX7", false, false, false, 17, 513, 0x326F, 0x326F,
                "C7F7C280FF70B508FCA248754BC445909C5B8A68798C7DC5988932068E4E2EF8"
            }
        };

    [Theory]
    [MemberData(nameof(ReferenceVectors))]
    public void Compression_IsByteExactWithUpstream(
        string _,
        string algorithm,
        bool backwards,
        bool quick,
        bool embedded,
        int skip,
        int expectedSize,
        int expectedLoad,
        int expectedExec,
        string expectedHash)
    {
        TapeRecord source = CreateReferenceRecord();
        var options = new MzfCompressionOptions(
            algorithm == "ZX0" ? MzfCompressionAlgorithm.Zx0 : MzfCompressionAlgorithm.Zx7,
            backwards ? CompressionDirection.Backward : CompressionDirection.Forward,
            quick,
            embedded,
            skip);

        MzfCompressionResult result = MzfCompressionService.Compress(
            source,
            options,
            CompressionTarget.MzfTape);
        byte[] serialized = TapeDocumentWriter.SerializeMzf(result.Record, preserveTrailing: false);

        Assert.Equal(expectedSize, result.Record.Header.MzfSize);
        Assert.Equal(expectedSize, result.Record.Body.DataSize);
        Assert.Equal(expectedSize, result.Record.Body.MzfBody.Length);
        Assert.Equal(expectedLoad, result.Record.Header.MzfStart);
        Assert.Equal(expectedExec, result.Record.Header.MzfExec);
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(serialized)));
    }

    [Fact]
    public void Compression_DoesNotMutateSourceAndDoesNotCompressTrailingData()
    {
        TapeRecord source = CreateReferenceRecord();
        MZQFileBody body = source.Body;
        body.TrailingData = [0xDE, 0xAD, 0xBE, 0xEF];
        source.Body = body;
        source.Profile = TapeProfile.Tc1_3;
        source.MetadataOrigin = MetadataOrigin.LoadedFromMfi;
        byte[] originalHeader = source.GetSerializedHeader();
        byte[] originalBody = (byte[])source.Body.MzfBody.Clone();

        MzfCompressionResult withTrailing = MzfCompressionService.Compress(
            source,
            new(MzfCompressionAlgorithm.Zx7),
            CompressionTarget.MzfTape);
        TapeRecord withoutTrailingSource = CreateReferenceRecord();
        MzfCompressionResult withoutTrailing = MzfCompressionService.Compress(
            withoutTrailingSource,
            new(MzfCompressionAlgorithm.Zx7),
            CompressionTarget.MzfTape);

        Assert.Equal(originalHeader, source.GetSerializedHeader());
        Assert.Equal(originalBody, source.Body.MzfBody);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, source.Body.TrailingData);
        Assert.Equal(withoutTrailing.Record.Body.MzfBody, withTrailing.Record.Body.MzfBody);
        Assert.Equal(source.Body.TrailingData, withTrailing.Record.Body.TrailingData);
        Assert.Equal(TapeProfile.Tc1_3, withTrailing.Record.Profile);
        Assert.Equal(MetadataOrigin.LoadedFromMfi, withTrailing.Record.MetadataOrigin);
    }

    [Fact]
    public void IplTarget_RejectsEmbeddedAndPartialCompression()
    {
        TapeRecord source = CreateReferenceRecord();

        InvalidOperationException embedded = Assert.Throws<InvalidOperationException>(() =>
            MzfCompressionService.Compress(
                source,
                new(MzfCompressionAlgorithm.Zx7, Zx7EmbeddedLoader: true),
                CompressionTarget.IplDsk));
        InvalidOperationException partial = Assert.Throws<InvalidOperationException>(() =>
            MzfCompressionService.Compress(
                source,
                new(MzfCompressionAlgorithm.Zx0, SkipBytes: 1),
                CompressionTarget.IplDsk));

        Assert.Contains("MZF header is not loaded", embedded.Message);
        Assert.Contains("Partial/skip", partial.Message);
    }

    [Fact]
    public void Auto_SelectsSmallestSafeCandidateAndPreservesSource()
    {
        TapeRecord source = CreateReferenceRecord();
        byte[] original = TapeDocumentWriter.SerializeMzf(source, preserveTrailing: true);

        MzfCompressionResult result = MzfCompressionService.Compress(
            source,
            new(MzfCompressionAlgorithm.Auto),
            CompressionTarget.IplDsk);

        Assert.Equal(MzfCompressionAlgorithm.Zx0, result.AppliedOptions.Algorithm);
        Assert.False(result.AppliedOptions.Zx0Quick);
        Assert.Equal(503, result.PackedSize);
        Assert.Equal(original, TapeDocumentWriter.SerializeMzf(source, preserveTrailing: true));
    }

    [Theory]
    [InlineData(false, false, 3325, 0x4373, 0x4373, "23192A9610F318E3D4387CD6CF86E32A50A90C9687DF82754905194E5E087415")]
    [InlineData(true, false, 4223, 0x3FF1, 0x3FF1, "FFF4C33506725EC2F5915FF6352EC0EDA4D61848987CF6FF7F125E358DE41C90")]
    [InlineData(true, true, 4227, 0x3FEE, 0x5001, "EE0E8CDCADA7FCF78BC40F63F203728134DB0C651CA791C1BC48F026376B151F")]
    public void Zx0Quick_UsesTheUpstreamLimitedOffsetParser(
        bool quick,
        bool backwards,
        int expectedSize,
        int expectedLoad,
        int expectedExec,
        string expectedHash)
    {
        TapeRecord source = CreateQuickDifferenceRecord();
        var options = new MzfCompressionOptions(
            MzfCompressionAlgorithm.Zx0,
            backwards ? CompressionDirection.Backward : CompressionDirection.Forward,
            Zx0Quick: quick);

        TapeRecord packed = MzfCompressionService.Compress(
            source,
            options,
            CompressionTarget.MzfTape).Record;
        byte[] serialized = TapeDocumentWriter.SerializeMzf(packed, preserveTrailing: false);

        Assert.Equal(expectedSize, packed.Header.MzfSize);
        Assert.Equal(expectedLoad, packed.Header.MzfStart);
        Assert.Equal(expectedExec, packed.Header.MzfExec);
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(serialized)));
    }

    private static TapeRecord CreateReferenceRecord()
    {
        byte[] body = new byte[1024];
        for (int index = 0; index < body.Length; index++)
        {
            body[index] = (byte)(((index % 97) * 13 + (index / 31) * 7) & 0xFF);
        }
        byte[] description = new byte[104];
        for (int index = 0; index < description.Length; index++)
        {
            int sourceIndex = index + 24;
            description[index] = (byte)((sourceIndex * 37 + 11) & 0xFF);
        }
        return CompressionTestData.ReadRecord(CompressionTestData.CreateMzf(
            body,
            description,
            name: "SYNTHVECTOR",
            load: 0x3000,
            exec: 0x3123));
    }

    private static TapeRecord CreateQuickDifferenceRecord()
    {
        byte[] prefix = CreateHashStream(900, 0);
        byte[] middle = CreateHashStream(2296, 1000);
        byte[] body = new byte[4096];
        prefix.CopyTo(body, 0);
        middle.CopyTo(body, 900);
        prefix.CopyTo(body, 3196);
        return CompressionTestData.ReadRecord(CompressionTestData.CreateMzf(
            body,
            name: "QUICKDIFF",
            load: 0x4000,
            exec: 0x4000));
    }

    private static byte[] CreateHashStream(int length, int seedBase)
    {
        byte[] result = new byte[length];
        for (int offset = 0; offset < length; offset += 32)
        {
            byte[] hash = SHA256.HashData(BitConverter.GetBytes(seedBase + offset / 32));
            Array.Copy(hash, 0, result, offset, Math.Min(hash.Length, length - offset));
        }
        return result;
    }
}
