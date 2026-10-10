using System.Linq;

namespace MZTools;

internal static class AudioRecoveryHistogram
{
    // Selecting one consumed stream avoids counting a physical pulse again for
    // each detector, threshold, polarity and classification sweep.
    internal static string Detector(AudioRegionRecoveryResult result, AudioRecoveryPass pass) =>
        pass.Measurements.Select(m => m.Block.DecodeDetector).Distinct()
            .OrderByDescending(name => result.Candidates.Count(c => c.ChecksumValid && c.PulseMode.ToString() == name))
            .FirstOrDefault() ?? "";
}
