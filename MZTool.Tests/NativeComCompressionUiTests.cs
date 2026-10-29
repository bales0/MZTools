using System.Security.Cryptography;
using System.Text.Json;

namespace MZTools.Tests;

public sealed class NativeComCompressionUiTests
{
    [Fact]
    public void SourceDescriptionUsesRecognizedAlgorithmDirectionAndLoaderForMzfAndM12()
    {
        string folder = TapeTestData.CreateTempDirectory();
        try
        {
            foreach (var source in NativeComCompressionData.Sources())
            {
                byte[] bytes = source.Record.GetSerializedHeader().Concat(source.Record.Body.MzfBody).ToArray();
                foreach (string extension in new[] { ".mzf", ".m12" })
                {
                    string path = Path.Combine(folder, "source" + extension);
                    File.WriteAllBytes(path, bytes);
                    var image = MzfNativeImageParser.Read(path);
                    Assert.Equal(extension == ".m12" ? "M12" : "MZF", image.SourceType);
                    if (source.Name == "plain")
                    {
                        Assert.Null(image.Compression);
                        Assert.Equal("Source compression: Not detected (none or unsupported)", NativeComSourceCompression.Text(image));
                        Assert.Equal(NativeComSourceCompression.UnknownToolTip, NativeComSourceCompression.ToolTip(image));
                    }
                    else
                    {
                        var info = Assert.IsType<MzfCompressionInfo>(image.Compression);
                        Assert.Equal($"{info.Algorithm}-{info.Direction}-{info.EmbeddedLoader}", source.Name);
                        Assert.Equal($"Source compression: {info.Algorithm.ToString().ToUpperInvariant()} · {info.Direction} · {(info.EmbeddedLoader ? "Embedded loader" : "Separate loader")}",
                            NativeComSourceCompression.Text(image));
                    }
                    Assert.Equal(bytes, File.ReadAllBytes(path));
                }
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void UnrecognizedCustomLoaderIsNotReportedAsUncompressedAndMissingSourceClearsDetails()
    {
        var record = NativeComCompressionData.Source();
        record.Body.MzfBody[0] = 0xC3; // Unknown entry trampoline is not a supported ZX loader signature.
        var image = MzfNativeImageParser.Parse(record);
        Assert.Null(image.Compression);
        Assert.Equal("Source compression: Not detected (none or unsupported)", NativeComSourceCompression.Text(image));
        Assert.Contains("Other or custom compression may be present", NativeComSourceCompression.ToolTip(image));
        Assert.Equal("Source compression: —", NativeComSourceCompression.Text(null));
        Assert.Null(NativeComSourceCompression.ToolTip(null));
    }

    [Fact]
    public void ComBytesMatchPreUiChangeBaselineForEverySourceAndPolicy()
    {
        // Generated before production edits from ff2ddbba00633a9113fa8957784e4ef3133518d3.
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "ReferenceGolden", "native-com-compression.json")))!;
        int verified = 0;
        foreach (var source in NativeComCompressionData.Sources())
        {
            byte[] before = source.Record.GetSerializedHeader().Concat(source.Record.Body.MzfBody).ToArray();
            foreach (var policy in NativeComCompressionData.Policies())
            {
                var image = MzfNativeImageParser.Parse(source.Record);
                var result = NativeComConversionService.Build(image, CpmTargetProfile.BuiltIns[0],
                    NativeMachineMode.Mz800Monitor, policy.Options);
                Assert.Equal(expected[source.Name + "/" + policy.Name], Convert.ToHexString(SHA256.HashData(result.Bytes)));
                Assert.Equal(before, source.Record.GetSerializedHeader().Concat(source.Record.Body.MzfBody).ToArray());
                verified++;
            }
        }
        Assert.Equal(49, verified);
        Assert.Equal(expected.Count, verified);
    }
}
