namespace QDTool
{
    internal enum MzfCompressionAlgorithm
    {
        None,
        Auto,
        Zx0,
        Zx7
    }

    internal enum CompressionDirection
    {
        Forward,
        Backward
    }

    internal enum CompressionTarget
    {
        MzfTape,
        MztTape,
        IplDsk
    }

    internal sealed record MzfCompressionOptions(
        MzfCompressionAlgorithm Algorithm,
        CompressionDirection Direction = CompressionDirection.Forward,
        bool Zx0Quick = false,
        bool Zx7EmbeddedLoader = false,
        int SkipBytes = 0);

    internal sealed record MzfCompressionResult(
        TapeRecord Record,
        MzfCompressionOptions AppliedOptions,
        int OriginalSize)
    {
        public int PackedSize => Record.Body.MzfBody.Length;
    }

    internal readonly record struct CompressionPayload(byte[] Data, int Delta);
}
