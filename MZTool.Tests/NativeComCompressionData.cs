namespace MZTools.Tests;

internal static class NativeComCompressionData
{
    internal static TapeRecord Source()
    {
        var record = TapeTestData.ReadRecord(TapeTestData.CreateMzf(
            Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray()));
        var header = record.Header;
        header.MzfStart = header.MzfExec = 0x3000;
        record.Header = header;
        return record;
    }

    internal static IEnumerable<(string Name, TapeRecord Record)> Sources()
    {
        yield return ("plain", Source());
        foreach (var algorithm in new[] { MzfCompressionAlgorithm.Zx0, MzfCompressionAlgorithm.Zx7 })
            foreach (var direction in Enum.GetValues<CompressionDirection>())
                foreach (bool embedded in algorithm == MzfCompressionAlgorithm.Zx7 ? new[] { false, true } : new[] { false })
                    yield return ($"{algorithm}-{direction}-{embedded}", MzfCompressionService.Compress(Source(),
                        new(algorithm, direction, Zx0Quick: algorithm == MzfCompressionAlgorithm.Zx0,
                            Zx7EmbeddedLoader: embedded), CompressionTarget.MzfTape).Record);
    }

    internal static IEnumerable<(string Name, MzfCompressionOptions? Options)> Policies()
    {
        yield return ("KeepSource", null);
        yield return ("None", new(MzfCompressionAlgorithm.None));
        yield return ("Auto", new(MzfCompressionAlgorithm.Auto));
        foreach (var algorithm in new[] { MzfCompressionAlgorithm.Zx0, MzfCompressionAlgorithm.Zx7 })
            foreach (var direction in Enum.GetValues<CompressionDirection>())
                yield return ($"{algorithm}-{direction}", new(algorithm, direction,
                    Zx0Quick: algorithm == MzfCompressionAlgorithm.Zx0));
    }
}
