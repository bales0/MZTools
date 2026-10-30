namespace MZTools {
internal static class CompressionDescription {
        internal static string Describe(MzfCompressionOptions options)
        {
            if (options.Algorithm is MzfCompressionAlgorithm.None or MzfCompressionAlgorithm.Auto) return options.Algorithm.ToString();
            return options.Algorithm.ToString().ToUpperInvariant() + (options.Zx0Quick ? " quick" : "") +
                (options.Direction == CompressionDirection.Backward ? " backward" : " forward") +
                (options.Zx7EmbeddedLoader ? ", embedded" : "") + (options.SkipBytes > 0 ? $", skip {options.SkipBytes}" : "");
        }

}}
