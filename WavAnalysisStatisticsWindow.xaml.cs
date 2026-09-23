using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        internal WavAnalysisStatisticsWindow(
            IReadOnlyList<AudioFileImportResult> results,
            bool includeDetails = false)
        {
            InitializeComponent();
            Title = results.Count == 1
                ? "Audio import summary"
                : "Audio batch analysis summary";
            MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 32);
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
            Width = Math.Min(Width, MaxWidth);
            Height = Math.Min(Height, MaxHeight);
            statisticsText.Text = BuildBatchText(results, includeDetails);
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
                $"failed {statistics.Failures.Count:N0}, " +
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
                    $"polarity: {(header.Inverted ? "Inverted" : "Normal")}    " +
                    $"evidence: {FormatProfileEvidence(header.ProfileEvidence)}" +
                    (recovery.ReconstructionUsed ? "    [reconstructed payload]" : string.Empty) +
                    (recovery.SelectiveRecoveryUsed ? "    [selective recovery]" : string.Empty));
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

            AppendFailures(text, statistics.Failures, statistics.Format.SampleRate);

            return text.ToString();
        }

        internal static string BuildBatchText(
            IReadOnlyList<AudioFileImportResult> results,
            bool includeDetails = false)
        {
            var text = new StringBuilder();
            int imported = results.Count(value => value.Imported);
            int partial = results.Count(value => value.Imported && value.Analysis?.Failures.Count > 0);
            int failed = results.Count - imported;
            text.AppendLine($"Files: {results.Count}    Imported: {imported}    Partial: {partial}    Failed: {failed}");
            text.AppendLine();
            text.AppendLine("File                          Records  Recovered  Failed  Recovery  Source  Duration");
            text.AppendLine(new string('-', 94));
            foreach (AudioFileImportResult result in results)
            {
                WavHeuristicStatistics? statistics = result.Analysis?.Statistics;
                string recovery = result.StandardFallbackUsed
                    ? "standard"
                    : statistics?.SelectiveRecoveryUsed == true ? "selective" : "no";
                string duration = statistics == null ? "—" : FormatDuration(statistics.DurationSeconds);
                int reconstructed = statistics?.ReconstructedRecords ?? 0;
                int failures = statistics?.Failures.Count ?? (result.Error == null ? 0 : 1);
                text.AppendLine(
                    $"{TrimTo(Path.GetFileName(result.SourceFile), 29),-29} " +
                    $"{result.Records.Count,7}  {reconstructed,9}  {failures,6}  " +
                    $"{recovery,-9} {statistics?.SourceFormat ?? "—",-7} {duration,10}");
            }

            foreach (AudioFileImportResult result in results)
            {
                text.AppendLine();
                text.AppendLine(Path.GetFileName(result.SourceFile));
                if (result.Error != null)
                {
                    text.AppendLine($"  ERROR: {result.Error.Message}");
                    continue;
                }
                if (result.Cancelled)
                {
                    text.AppendLine("  Cancelled.");
                    continue;
                }
                if (result.StandardFallbackUsed)
                {
                    text.AppendLine("  Standard checksum-valid decoder supplied the imported records.");
                }
                if (result.Analysis is { } analysis)
                {
                    for (int index = 0; index < analysis.Statistics.RecordRecoveries.Count; index++)
                    {
                        WavRecoveryInfo recovery = analysis.Statistics.RecordRecoveries[index];
                        string name = index < analysis.Records.Count
                            ? SharpMzEncoding.ConvertMzfNameToASCIIString(analysis.Records[index].Header.MzfFname)
                            : $"Record {index + 1}";
                        text.AppendLine(
                            $"  {index + 1,2}. {name,-16}  {TapeProfileNames.ToDisplayName(recovery.FinalProfile),-12}  " +
                            $"{(recovery.Header.Candidate.Inverted ? "Inverted" : "Normal"),-8}  " +
                            $"H:{(recovery.Header.Candidate.ChecksumValid ? "OK" : "bad")} " +
                            $"P:{(recovery.Payload.Candidate.ChecksumValid ? "OK" : "bad")}" +
                            (recovery.ReconstructionUsed ? " reconstructed" : string.Empty));
                    }
                    AppendFailures(text, analysis.Failures, analysis.Statistics.Format.SampleRate);
                }
                else
                {
                    for (int index = 0; index < result.Records.Count; index++)
                    {
                        TapeRecord record = result.Records[index];
                        string name = SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname);
                        text.AppendLine(
                            $"  {index + 1,2}. {name,-16}  {TapeProfileNames.ToDisplayName(record.Profile)}");
                    }
                }
            }
            return text.ToString();
        }

        private static void AppendFailures(
            StringBuilder text,
            IReadOnlyList<WavAnalysisFailure> failures,
            uint sampleRate)
        {
            if (failures.Count == 0)
            {
                return;
            }
            text.AppendLine();
            text.AppendLine("Unresolved programs:");
            foreach (WavAnalysisFailure failure in failures)
            {
                double position = sampleRate == 0 ? 0 : failure.StartSample / (double)sampleRate;
                text.AppendLine(
                    $"  {FormatDuration(position)}  expected {failure.ExpectedLength?.ToString() ?? "?"} B  " +
                    $"polarity {(failure.Inverted.HasValue ? (failure.Inverted.Value ? "Inverted" : "Normal") : "undetermined")}  " +
                    $"{failure.Reason}");
            }
        }

        private static string TrimTo(string value, int length) =>
            value.Length <= length ? value : value[..(length - 1)] + "…";

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
