using System;
using System.Text;
using System.Windows;

namespace QDTool
{
    public partial class WavAnalysisStatisticsWindow : Window
    {
        internal WavAnalysisStatisticsWindow(WavHeuristicStatistics statistics)
        {
            InitializeComponent();
            MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 32);
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
            Width = Math.Min(Width, MaxWidth);
            Height = Math.Min(Height, MaxHeight);
            statisticsText.Text = BuildText(statistics);
        }

        private static string BuildText(WavHeuristicStatistics statistics)
        {
            PcmAudioFormat format = statistics.Format;
            var text = new StringBuilder();

            text.AppendLine($"Source: {statistics.SourceFormat}    Duration: {FormatDuration(statistics.DurationSeconds)}");
            text.AppendLine(
                $"Format: {format.SampleRate:N0} Hz / {format.BitsPerSample} bit / " +
                $"{format.Channels} channel(s) / {format.FrameCount:N0} frames");
            text.AppendLine("Signal polarity: detected independently for each record");
            text.AppendLine();
            text.AppendLine(
                $"Candidates: header {statistics.HeaderCandidates:N0}, payload {statistics.PayloadCandidates:N0}, " +
                $"valid payload {statistics.ValidPayloadCandidates:N0}");
            text.AppendLine(
                $"Result: {statistics.ResultRecords:N0} record(s), reconstructed {statistics.ReconstructedRecords:N0}, " +
                $"selective recovery {(statistics.SelectiveRecoveryUsed ? "used" : "not needed")}");
            text.AppendLine();
            text.AppendLine("Detected records:");

            for (int index = 0; index < statistics.RecordRecoveries.Count; index++)
            {
                WavRecoveryInfo recovery = statistics.RecordRecoveries[index];
                SharpBlockCandidate header = recovery.Header.Candidate;
                SharpBlockCandidate payload = recovery.Payload.Candidate;

                text.AppendLine();
                text.AppendLine(
                    $"{index + 1}. {TapeProfileNames.ToDisplayName(recovery.FinalProfile)}    " +
                    $"evidence: {FormatProfileEvidence(header.ProfileEvidence)}" +
                    (recovery.ReconstructionUsed ? "    [reconstructed payload]" : string.Empty));
                text.AppendLine(
                    $"   Header : {FormatSource(header)}");
                text.AppendLine(
                    $"   Payload: {FormatSource(payload)}");

                AppendTiming(text, "H", header);
                AppendTiming(text, "P", payload);

                if (recovery.Native1xAnalysis is Native1xTimingAnalysis native)
                {
                    text.AppendLine(
                        $"   Native 1:1 ratios: H {FormatRatio(native.HeaderPeriodRatio)}, " +
                        $"P {FormatRatio(native.PayloadPeriodRatio)}");
                    text.AppendLine(
                        $"   Native 1:1 fit: MZ800 H/P {FormatPercent(native.HeaderMz800Error)}/" +
                        $"{FormatPercent(native.PayloadMz800Error)}, MZ700 H/P " +
                        $"{FormatPercent(native.HeaderMz700Error)}/{FormatPercent(native.PayloadMz700Error)}");
                    text.AppendLine(
                        $"   Combined fit: MZ800 {FormatPercent(native.CombinedMz800Error)}, " +
                        $"MZ700 {FormatPercent(native.CombinedMz700Error)} -> " +
                        TapeProfileNames.ToDisplayName(native.ResultProfile));
                }
            }

            return text.ToString();
        }

        private static void AppendTiming(StringBuilder text, string prefix, SharpBlockCandidate candidate)
        {
            if (!double.IsFinite(candidate.TimingShortHighMicroseconds) ||
                !double.IsFinite(candidate.TimingShortLowMicroseconds) ||
                !double.IsFinite(candidate.TimingLongHighMicroseconds) ||
                !double.IsFinite(candidate.TimingLongLowMicroseconds))
            {
                return;
            }

            double ratio = PeriodRatio(candidate);
            text.AppendLine(
                $"   {prefix} timing: SHORT {FormatTiming(candidate.TimingShortHighMicroseconds)}/" +
                $"{FormatTiming(candidate.TimingShortLowMicroseconds)} us, LONG " +
                $"{FormatTiming(candidate.TimingLongHighMicroseconds)}/" +
                $"{FormatTiming(candidate.TimingLongLowMicroseconds)} us, L/S {FormatRatio(ratio)}");
        }

        private static string FormatSource(SharpBlockCandidate candidate) =>
            $"ch {candidate.Channel + 1}, {FormatPulseMode(candidate.PulseMode)}, copy {candidate.CopyIndex + 1}, " +
            $"{(candidate.Inverted ? "inverted" : "normal")}, checksum {(candidate.ChecksumValid ? "OK" : "bad")}";

        private static string FormatPulseMode(WavPulseMode mode) => mode switch
        {
            WavPulseMode.ZeroCrossing => "zero-cross",
            WavPulseMode.Schmitt => "Schmitt",
            _ => mode.ToString()
        };

        private static double PeriodRatio(SharpBlockCandidate candidate)
        {
            double shortPeriod = candidate.TimingShortHighMicroseconds + candidate.TimingShortLowMicroseconds;
            double longPeriod = candidate.TimingLongHighMicroseconds + candidate.TimingLongLowMicroseconds;
            return shortPeriod > 0 ? longPeriod / shortPeriod : double.NaN;
        }

        private static string FormatTiming(double value) =>
            double.IsFinite(value) ? value.ToString("F3") : "n/a";

        private static string FormatRatio(double value) =>
            double.IsFinite(value) ? value.ToString("F5") : "n/a";

        private static string FormatPercent(double value) =>
            double.IsFinite(value) ? $"{value * 100.0:F2}%" : "n/a";

        private static string FormatProfileEvidence(SharpProfileEvidence evidence) => evidence switch
        {
            SharpProfileEvidence.TurboCopyHeaderAndLoader => "TC header + checksum-valid loader",
            SharpProfileEvidence.IntercopyHeader => "IC header structure",
            SharpProfileEvidence.StructuredMz700 => "MZ700 FAST3 loader structure",
            _ => "timing only"
        };

        internal static string FormatDuration(double durationSeconds)
        {
            long totalTenths = checked((long)Math.Round(
                durationSeconds * 10,
                MidpointRounding.AwayFromZero));
            long hours = totalTenths / 36000;
            long minutes = (totalTenths / 600) % 60;
            long seconds = (totalTenths / 10) % 60;
            long tenths = totalTenths % 10;
            return $"{hours}:{minutes:00}:{seconds:00},{tenths}";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
