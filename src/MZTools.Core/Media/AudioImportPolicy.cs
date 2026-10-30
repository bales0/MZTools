namespace MZTools {
    internal static class AudioImportPolicy
    {
        public static bool ShouldTryStandardDecoder(WavHeuristicAnalysisResult analysis) =>
            ShouldTryStandardDecoder(analysis.Failures.Count);

        internal static bool ShouldTryStandardDecoder(int failureCount) => failureCount > 0;
    }

}

