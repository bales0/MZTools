using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Collections;
using System.Windows.Controls;
using System.Windows.Data;

namespace MZTools
{
    public partial class WavAnalysisStatisticsWindow : Window
    {
        private string? visualSource;
        private void VisualAnalysis_Click(object sender, RoutedEventArgs e) => new AudioSignalAnalyzerWindow(this, visualSource).Show();
        internal WavAnalysisStatisticsWindow(WavHeuristicStatistics statistics)
        {
            InitializeComponent();
            visualSource = statistics.SourceFile;
            MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 32);
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
            Width = Math.Min(Width, MaxWidth);
            Height = Math.Min(Height, MaxHeight);
            ShowReport(new[] { (statistics.SourceFile, (IReadOnlyList<TapeRecord>)Array.Empty<TapeRecord>(), (WavHeuristicStatistics?)statistics, "Analyzed", (string?)null) });
        }

        internal WavAnalysisStatisticsWindow(
            IReadOnlyList<AudioFileImportResult> results,
            bool includeDetails = false)
        {
            InitializeComponent();
            visualSource = results.Count == 1 ? results[0].SourceFile : null;
            Title = results.Count == 1
                ? "Audio import summary"
                : "Audio batch analysis summary";
            MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 32);
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
            Width = Math.Min(Width, MaxWidth);
            Height = Math.Min(Height, MaxHeight);
            ShowReport(results.Select(r => (r.SourceFile, r.Records, r.Analysis?.Statistics,
                r.Cancelled ? "Cancelled" : r.Error != null ? "Failed" : r.Imported ? r.Analysis?.Failures.Count > 0 ? "Partial" : "Imported" : "No programs",
                r.Error?.Message ?? (r.StandardFallbackUsed ? "Standard checksum-valid decoder supplied the imported records." : null))).ToArray());
            reportSummary.Text = $"Files: {results.Count} • Imported: {results.Count(r => r.Imported)} • Partial: {results.Count(r => r.Imported && r.Analysis?.Failures.Count > 0)} • Failed: {results.Count(r => !r.Imported && !r.Cancelled)} • Cancelled: {results.Count(r => r.Cancelled)}";
        }

        private void ShowReport(IReadOnlyList<(string Source, IReadOnlyList<TapeRecord> Records, WavHeuristicStatistics? Statistics, string Status, string? Message)> sources)
        {
            reportHeading.Text = sources.Count == 1 ? Path.GetFileName(sources[0].Source) : "Audio import results";
            var files = new List<object>(); var programs = new List<object>(); var properties = new List<object>();
            var messages = new List<object>(); var blocks = new List<object>();
            foreach (var source in sources)
            {
                string file = Path.GetFileName(source.Source); var s = source.Statistics;
                files.Add(new { File = file, source.Status, Records = s?.ResultRecords ?? source.Records.Count,
                    Recovered = s?.ReconstructedRecords ?? 0, Failed = s?.Failures.Count ?? (source.Status == "Failed" ? 1 : 0),
                    Format = s?.SourceFormat ?? "—", Duration = s == null ? "—" : FormatDuration(s.DurationSeconds), Source = source.Source });
                void Property(string name, object? value) => properties.Add(new { File = file, Property = name, Value = value?.ToString() ?? "—" });
                void Message(string category, string message, long? start = null, long? end = null) => messages.Add(new { File = file, Category = category, Message = message,
                    StartSeconds = s == null || !start.HasValue ? (double?)null : start.Value / (double)s.Format.SampleRate,
                    EndSeconds = s == null || !end.HasValue ? (double?)null : end.Value / (double)s.Format.SampleRate });
                Property("Source", source.Source);
                if (source.Message != null) Message(source.Status, source.Message);
                if (source.Status == "Cancelled") Message("Result", "Import cancelled.");
                if (s != null)
                {
                    Property("Sample rate (Hz)", s.Format.SampleRate); Property("Bit depth", s.Format.BitsPerSample);
                    Property("Channels", s.Format.Channels); Property("Frames", s.Format.FrameCount);
                    Property("Header candidates", s.HeaderCandidates); Property("Payload candidates", s.PayloadCandidates);
                    Property("Valid payload candidates", s.ValidPayloadCandidates); Property("Selective recovery", s.SelectiveRecoveryUsed ? "Used" : "Not needed");
                    Property("Rejected checksum-valid headers", s.RejectedHeaders.Count + s.OmittedHeaderRejections);
                    foreach (var rejected in s.RejectedHeaders) Message("Header rejected",
                        $"{rejected.Reason} Channel {rejected.Channel}; {rejected.Detector}; inverted {rejected.Inverted}; checksum {rejected.RecordedChecksum:X4}/{rejected.CalculatedChecksum:X4}; header SHA-256 {rejected.HeaderSha256}.", rejected.Sample, rejected.Sample);
                    if (s.OmittedHeaderRejections > 0) Message("Header rejected", $"{s.OmittedHeaderRejections} additional rejected-header events omitted (bounded diagnostics).");
                    foreach (var failure in s.Failures) Message("Unresolved", failure.Reason + (failure.ExpectedLength.HasValue ? $" Expected {failure.ExpectedLength} B." : ""), failure.StartSample, failure.EndSample);
                }
                int count = Math.Max(source.Records.Count, s?.RecordRecoveries.Count ?? 0);
                for (int i = 0; i < count; i++)
                {
                    var record = i < source.Records.Count ? source.Records[i] : null;
                    var recovery = i < (s?.RecordRecoveries.Count ?? 0) ? s!.RecordRecoveries[i] : null;
                    string name = record != null ? SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname)
                        : recovery != null ? SharpMzEncoding.ConvertMzfNameToASCIIString(recovery.Header.Candidate.Data.Skip(1).Take(17).ToArray()) : $"Record {i + 1}";
                    programs.Add(new { File = file, Record = i + 1, Name = name,
                        Profile = TapeProfileNames.ToDisplayName(recovery?.FinalProfile ?? record!.Profile),
                        Polarity = recovery == null ? "—" : recovery.Header.Candidate.Inverted ? "Inverted" : "Normal",
                        HeaderChecksum = recovery == null ? "—" : recovery.Header.Candidate.ChecksumValid ? "OK" : "Failed",
                        PayloadChecksum = recovery == null ? "—" : recovery.Payload.Candidate.ChecksumValid ? "OK" : "Failed",
                        Reconstructed = recovery?.ReconstructionUsed ?? false, SelectiveRecovery = recovery?.SelectiveRecoveryUsed ?? false });
                    if (recovery == null) continue;
                    foreach (var c in new[] { recovery.Header.Candidate, recovery.Payload.Candidate })
                        blocks.Add(new { File = file, Record = i + 1, Block = c.Kind.ToString(), Channel = c.Channel + 1,
                            Detector = FormatPulseMode(c.PulseMode), Copy = c.CopyIndex + 1, c.Inverted, c.ChecksumValid,
                            Evidence = FormatProfileEvidence(c.ProfileEvidence), c.StartSample, c.EndSample,
                            ShortHighUs = double.IsFinite(c.TimingShortHighMicroseconds) ? (double?)c.TimingShortHighMicroseconds : null,
                            ShortLowUs = double.IsFinite(c.TimingShortLowMicroseconds) ? (double?)c.TimingShortLowMicroseconds : null,
                            LongHighUs = double.IsFinite(c.TimingLongHighMicroseconds) ? (double?)c.TimingLongHighMicroseconds : null,
                            LongLowUs = double.IsFinite(c.TimingLongLowMicroseconds) ? (double?)c.TimingLongLowMicroseconds : null });
                    if (recovery.Native1xAnalysis is Native1xTimingAnalysis native)
                    {
                        Property($"Record {i + 1}: native 1:1 H/P ratios", $"{FormatRatio(native.HeaderPeriodRatio)} / {FormatRatio(native.PayloadPeriodRatio)}");
                        Property($"Record {i + 1}: MZ800 H/P fit", $"{FormatPercent(native.HeaderMz800Error)} / {FormatPercent(native.PayloadMz800Error)}");
                        Property($"Record {i + 1}: MZ700 H/P fit", $"{FormatPercent(native.HeaderMz700Error)} / {FormatPercent(native.PayloadMz700Error)}");
                        Property($"Record {i + 1}: combined MZ800/MZ700 fit", $"{FormatPercent(native.CombinedMz800Error)} / {FormatPercent(native.CombinedMz700Error)}");
                        Property($"Record {i + 1}: native result", TapeProfileNames.ToDisplayName(native.ResultProfile));
                    }
                }
            }
            reportSummary.Text = $"Files: {sources.Count} • Programs: {programs.Count} • Messages: {messages.Count}";
            AddReportTable("Files", files); AddReportTable("Records", programs); AddReportTable("Properties", properties);
            AddReportTable($"Messages ({messages.Count})", messages); AddReportTable("Block details", blocks);
            reportTabs.SelectedIndex = programs.Count > 0 ? 1 : 3;
        }

        private void AddReportTable(string title, IEnumerable rows)
        {
            var items = rows.Cast<object>().ToArray();
            var typed = Array.CreateInstance(items.FirstOrDefault()?.GetType() ?? typeof(object), items.Length);
            Array.Copy(items, typed, items.Length);
            var table = new DataGrid { ItemsSource = typed, IsReadOnly = true, AutoGenerateColumns = true,
                CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeColumns = true,
                SelectionUnit = DataGridSelectionUnit.CellOrRowHeader, SelectionMode = DataGridSelectionMode.Extended,
                ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader, HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, EnableRowVirtualization = true, EnableColumnVirtualization = true };
            table.AutoGeneratingColumn += (_, e) =>
            {
                e.Column.Header = System.Text.RegularExpressions.Regex.Replace(e.PropertyName, "(?<=[a-z])(?=[A-Z])", " ");
                e.Column.MinWidth = 45; e.Column.CanUserResize = true;
                e.Column.Width = e.PropertyName switch { "File" => 220, "Name" => 180, "Profile" => 115, _ => 110 };
                if (e.PropertyName is "Message" or "Value" or "Source") e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                if (e.Column is DataGridTextColumn column && column.Binding is Binding binding)
                {
                    if ((Nullable.GetUnderlyingType(e.PropertyType) ?? e.PropertyType) == typeof(double)) binding.StringFormat = "0.####";
                    if (e.PropertyName is "Message" or "Value")
                    {
                        var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap)); column.ElementStyle = style;
                    }
                }
            };
            reportTabs.Items.Add(new TabItem { Header = title, Content = table });
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
