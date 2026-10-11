using System.Security.Cryptography;
using System.Text.Json;

namespace MZTools.Tests;

public class AudioReferenceBaselineTests
{
    [Fact]
    public void UnionWav_AllowsCombinedPayloadLargerThan64KiB()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        var first = TapeTestData.ReadRecord(TapeTestData.CreateMzf(new byte[32769],name:"FIRST"));
        var second = TapeTestData.ReadRecord(TapeTestData.CreateMzf(Enumerable.Repeat((byte)1,32769).ToArray(),name:"SECOND"));
        first.Profile = second.Profile = TapeProfile.Normal1_4;
        try {
            Assert.True(first.Body.MzfBody.Length + second.Body.MzfBody.Length > 0x10000);
            SharpTapeExporter.Export(path,[first,second],SharpTapeOutputFormat.Wav);
            var imported = SharpTapeImporter.ReadFile(path);
            Assert.Equal(2,imported.Count);
            Assert.Equal(first.Body.MzfBody,imported[0].Body.MzfBody);
            Assert.Equal(second.Body.MzfBody,imported[1].Body.MzfBody);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NormalWav_PreservesWrappedLoadRange()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        byte[] mzf = TapeTestData.CreateMzf(Enumerable.Range(0,32).Select(i=>(byte)i).ToArray(),name:"WRAPPED");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(20,2),0xFFF0);
        var record = TapeTestData.ReadRecord(mzf);
        try {
            SharpTapeExporter.Export(path,[record],SharpTapeOutputFormat.Wav);
            var imported = Assert.Single(SharpTapeImporter.ReadFile(path));
            Assert.Equal(record.GetSerializedHeader(),imported.GetSerializedHeader());
            Assert.Equal(record.Body.MzfBody,imported.Body.MzfBody);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData((int)TapeProfile.Ic1_4)]
    [InlineData((int)TapeProfile.Tc1_3)]
    [InlineData((int)TapeProfile.Mz700_1_3)]
    public void GeneratedLoaderFailure_IdentifiesRecordAndPreservesDestination(int profile)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        var valid = TapeTestData.ReadRecord(TapeTestData.CreateMzf([1],name:"VALID"));
        byte[] mzf = TapeTestData.CreateMzf(new byte[0x5480],name:"BAD RANGE");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(20,2),0xBA02);
        var invalid = TapeTestData.ReadRecord(mzf); invalid.Profile = (TapeProfile)profile;
        byte[] sentinel = [1,2,3]; File.WriteAllBytes(path,sentinel);
        try {
            var error = Assert.Throws<InvalidOperationException>(()=>SharpTapeExporter.Export(path,[valid,invalid],SharpTapeOutputFormat.Wav));
            Assert.Contains("record 2",error.Message); Assert.Contains("BAD RANGE",error.Message);
            Assert.Contains(TapeProfileNames.ToDisplayName(invalid.Profile),error.Message);
            Assert.Contains("LOAD $BA02",error.Message); Assert.Contains("21632 B / $5480",error.Message);
            Assert.Contains("$10E82",error.Message); Assert.Contains("16-bit address space",error.Message);
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Equal(sentinel,File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExportFingerprints_AllSupportedPwmProfilesRemainByteExact()
    {
        var fingerprints = new SortedDictionary<string, string>();
        foreach (var profile in Enum.GetValues<TapeProfile>().Where(p => p is not (TapeProfile.Ultra or TapeProfile.UltraMz700 or TapeProfile.UltraMz800)))
        foreach (var format in Enum.GetValues<SharpTapeOutputFormat>())
        {
            var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf([0, 255, 85, 170, 1, 2, 3, 4])); record.Profile = profile;
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tmp");
            try
            {
                SharpTapeExporter.Export(path, [record], format, SharpTapeMachine.Mz800);
                fingerprints[$"{profile}/{format}"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
            finally { File.Delete(path); }
        }
        string json = JsonSerializer.Serialize(fingerprints, new JsonSerializerOptions { WriteIndented = true });
        string? output = Environment.GetEnvironmentVariable("MZTOOLS_REFERENCE_FINGERPRINTS");
        if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, json);
        string golden = Path.Combine(AppContext.BaseDirectory, "ReferenceGolden", "export-fingerprints.json");
        Assert.True(File.Exists(golden), "Missing checked-in reference fingerprints.");
        Assert.Equal(File.ReadAllText(golden).Trim(), json.Trim());
        Assert.Equal(39, fingerprints.Count);
    }

    [Fact]
    public void AvailableHeaderlessFixture_PreservesDocumentedPayloadFingerprint()
    {
        string? path = Environment.GetEnvironmentVariable("MZTOOLS_P0_HEADERLESS_FIXTURE");
        if (string.IsNullOrEmpty(path)) return; // Availability is recorded separately in the audit report.
        byte[] original = SHA256.HashData(File.ReadAllBytes(path));
        Assert.Equal("68B2AFF62A90A85DF7336CF4C2D0EA763804AC018CD542D1D9385C8E5C52C362", Convert.ToHexString(original));
        using var reader = new WavPcmStreamReader(path);
        var result = WavHeuristicAnalyzer.AnalyzeRange(path, 0, reader.Format.FrameCount, 37744,
            options: new() { PayloadOnly = true, Channel = 1, ZeroCrossing = false, ThresholdScales = [.4] });
        Assert.Contains(result.Candidates, c => c.ChecksumValid && c.RecordedChecksum == 19836 &&
            Convert.ToHexString(SHA256.HashData(c.Data)) == "F8A2749D98DD0C90789C368103702DB2E3186EB5FF8E9AD323616E22809083FC");
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(path)));
    }
}
