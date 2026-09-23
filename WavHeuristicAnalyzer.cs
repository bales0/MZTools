using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace QDTool
{
    internal enum WavPulseMode
    {
        ZeroCrossing,
        Schmitt
    }

    internal enum SharpBlockKind
    {
        Header,
        Payload
    }

    internal enum SharpProfileEvidence
    {
        TimingOnly,
        StructuredMz700,
        IntercopyHeader,
        TurboCopyHeaderAndLoader
    }

    internal readonly record struct SharpZ80TimingReference(
        string Name,
        TapeProfile Profile,
        double ShortHighMicroseconds,
        double ShortLowMicroseconds,
        double LongHighMicroseconds,
        double LongLowMicroseconds,
        int HeaderLeaderPulses,
        int DataLeaderPulses,
        string Origin);

    // Authoritative waveform references from the ROM/Z80 analysis table.
    // Do not replace these values with WAV-derived or TapeMZ-derived timings.
    internal static class SharpZ80TimingTable
    {
        internal static readonly SharpZ80TimingReference Rom800Normal =
            new("NRL1:1 / IC header / TC header+loader", TapeProfile.Normal1_1,
                237.956, 258.819, 469.145, 487.189, 11000, 5500, "ROM800");
        internal static readonly SharpZ80TimingReference Ic1_1 =
            new("IC1:1 data", TapeProfile.Ic1_1,
                234.573, 263.894, 469.145, 494.802, 0, 5500, "IC1200");
        internal static readonly SharpZ80TimingReference Normal1_2 =
            new("NRL1:2", TapeProfile.Normal1_2,
                113.621, 139.278, 234.573, 260.229, 11000, 5500, "IC2400");
        internal static readonly SharpZ80TimingReference Ic1_2 =
            new("IC1:2 data", TapeProfile.Ic1_2,
                113.621, 139.278, 234.573, 260.229, 0, 5500, "IC2400");
        internal static readonly SharpZ80TimingReference Normal1_3 =
            new("NRL1:3", TapeProfile.Normal1_3,
                87.965, 124.617, 175.930, 223.577, 11000, 5500, "IC2800");
        internal static readonly SharpZ80TimingReference Ic1_3 =
            new("IC1:3 data", TapeProfile.Ic1_3,
                87.965, 124.617, 175.930, 223.577, 0, 5500, "IC2800");
        internal static readonly SharpZ80TimingReference Normal1_4 =
            new("NRL1:4", TapeProfile.Normal1_4,
                76.969, 117.286, 157.604, 179.595, 11000, 5500, "IC3200");
        internal static readonly SharpZ80TimingReference Ic1_4 =
            new("IC1:4 data", TapeProfile.Ic1_4,
                76.969, 117.286, 157.604, 179.595, 0, 5500, "IC3200");
        internal static readonly SharpZ80TimingReference Mz700Normal =
            new("MZ700 NRL1:1 / MZ7-3 header", TapeProfile.Mz700_1_1,
                240.000, 264.000, 464.000, 494.000, 11000, 5500, "ROM700");
        internal static readonly SharpZ80TimingReference Mz700Fast3 =
            new("MZ700 FAST3 data", TapeProfile.Mz700_1_3,
                80.000, 80.000, 160.000, 160.000, 0, 5500, "ROM700");
        internal static readonly SharpZ80TimingReference Tc1_1 =
            new("TC1:1 data", TapeProfile.Tc1_1,
                250.909, 250.000, 500.909, 500.909, 0, 5500, "TurboCopy");
        internal static readonly SharpZ80TimingReference Tc1_2 =
            new("TC1:2 data", TapeProfile.Tc1_2,
                141.818, 140.909, 282.727, 282.727, 0, 5500, "TurboCopy");
        internal static readonly SharpZ80TimingReference Tc1_3 =
            new("TC1:3 data", TapeProfile.Tc1_3,
                105.455, 104.545, 210.000, 210.000, 0, 5500, "TurboCopy");
        internal static readonly SharpZ80TimingReference UltraPreTransferRom800 =
            new("UL pre-transfer data/marks", TapeProfile.Ultra,
                237.956, 258.819, 469.145, 487.189, 0, 0, "ROM800");
        internal static readonly SharpZ80TimingReference Ultra800Leader =
            new("UL / UL800 leader", TapeProfile.UltraMz800,
                237.956, 258.819, double.NaN, double.NaN, 2000, 1000, "ROM800");
        internal static readonly SharpZ80TimingReference Ultra700Leader =
            new("UL700 leader", TapeProfile.UltraMz700,
                240.000, 264.000, double.NaN, double.NaN, 2000, 1000, "ROM700");

        // Only these profiles can be inferred from an ordinary checksummed MZ header
        // by timing alone. IC/TC are identified structurally and their data speed is
        // taken from the loader marker, not guessed from the header waveform.
        internal static readonly SharpZ80TimingReference[] HeaderTimingCandidates =
        [
            Rom800Normal,
            Normal1_2,
            Normal1_3,
            Normal1_4,
            Mz700Normal
        ];
    }

    internal readonly record struct SharpTimingMatch(
        TapeProfile Profile,
        double TapeSpeedScale,
        double RelativeError,
        double ShortHighMicroseconds,
        double ShortLowMicroseconds,
        double LongHighMicroseconds,
        double LongLowMicroseconds);

    internal sealed class SharpBlockCandidate
    {
        internal required SharpBlockKind Kind { get; init; }
        internal required int Channel { get; init; }
        internal required int CopyIndex { get; init; }
        internal required bool Inverted { get; init; }
        internal required WavPulseMode PulseMode { get; init; }
        internal required byte[] Data { get; init; }
        internal required bool ChecksumValid { get; init; }
        internal required ushort RecordedChecksum { get; init; }
        internal required ushort CalculatedChecksum { get; init; }
        internal required long StartSample { get; init; }
        internal required long EndSample { get; init; }
        internal required double LeaderAverage { get; init; }
        internal required double LeaderStdDev { get; init; }
        internal required double PulseConfidence { get; init; }
        internal double PolarityConfidence { get; init; }
        internal double TimingScale { get; init; } = 1.0;
        internal double TimingError { get; init; } = double.PositiveInfinity;
        internal double TimingShortHighMicroseconds { get; init; } = double.NaN;
        internal double TimingShortLowMicroseconds { get; init; } = double.NaN;
        internal double TimingLongHighMicroseconds { get; init; } = double.NaN;
        internal double TimingLongLowMicroseconds { get; init; } = double.NaN;
        internal TapeProfile Profile { get; init; } = TapeProfile.Normal1_1;
        internal SharpProfileEvidence ProfileEvidence { get; init; } = SharpProfileEvidence.TimingOnly;
    }

    internal sealed class WavBlockRecoveryInfo
    {
        internal required SharpBlockCandidate Candidate { get; init; }
    }

    internal readonly record struct Native1xTimingAnalysis(
        TapeProfile ResultProfile,
        double HeaderPeriodRatio,
        double PayloadPeriodRatio,
        double HeaderMz800Error,
        double HeaderMz700Error,
        double PayloadMz800Error,
        double PayloadMz700Error,
        double CombinedMz800Error,
        double CombinedMz700Error);

    internal sealed class WavRecoveryInfo
    {
        internal required WavBlockRecoveryInfo Header { get; init; }
        internal required WavBlockRecoveryInfo Payload { get; init; }
        internal required bool ReconstructionUsed { get; init; }
        internal required TapeProfile FinalProfile { get; init; }
        internal Native1xTimingAnalysis? Native1xAnalysis { get; init; }
        internal bool SelectiveRecoveryUsed { get; init; }
    }

    internal sealed record WavAnalysisFailure(
        string SourceFile,
        string Reason,
        long StartSample,
        long EndSample,
        ushort? ExpectedLength,
        bool? Inverted,
        bool HeaderFound,
        bool PayloadCandidatesFound,
        bool RecoveryAttempted);

    internal sealed class WavAssemblyResult
    {
        internal required IReadOnlyList<TapeRecord> Records { get; init; }
        internal required IReadOnlyList<WavRecoveryInfo> Recoveries { get; init; }
        internal required IReadOnlyList<WavAnalysisFailure> Failures { get; init; }
        internal int ReconstructedRecords => Recoveries.Count(value => value.ReconstructionUsed);
    }

    internal readonly record struct WavAnalysisProgress(
        string Stage,
        double Fraction,
        long ProcessedFrames,
        long TotalFrames,
        int CandidateCount);

    internal sealed class WavHeuristicStatistics
    {
        internal required string SourceFile { get; init; }
        internal required PcmAudioFormat Format { get; init; }
        internal required string SourceFormat { get; init; }
        internal required int HeaderCandidates { get; init; }
        internal required int PayloadCandidates { get; init; }
        internal required int ValidPayloadCandidates { get; init; }
        internal required int ResultRecords { get; init; }
        internal required int ReconstructedRecords { get; init; }
        internal required bool SelectiveRecoveryUsed { get; init; }
        internal required IReadOnlyList<WavRecoveryInfo> RecordRecoveries { get; init; }
        internal required IReadOnlyList<WavAnalysisFailure> Failures { get; init; }
        internal double DurationSeconds => Format.FrameCount / (double)Format.SampleRate;
    }

    internal sealed class WavHeuristicAnalysisResult
    {
        internal required IReadOnlyList<TapeRecord> Records { get; init; }
        internal required WavHeuristicStatistics Statistics { get; init; }
        internal required IReadOnlyList<WavAnalysisFailure> Failures { get; init; }
    }

    internal static class WavHeuristicAnalyzer
    {
        internal static IReadOnlyList<TapeRecord> ReadFile(string filePath) =>
            AnalyzeFile(filePath).Records;

        internal static WavHeuristicAnalysisResult AnalyzeFile(
            string filePath,
            IProgress<WavAnalysisProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            using IPcmAudioStreamReader reader = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".wav" => new WavPcmStreamReader(filePath),
                ".flac" => new FlacPcmStreamReader(filePath),
                _ => throw new ArgumentException(
                    $"Unsupported heuristic audio extension: {Path.GetExtension(filePath)}",
                    nameof(filePath))
            };
            var candidates = new List<SharpBlockCandidate>();
            var expectedLengths = new HashSet<int>();
            var channels = Enumerable.Range(0, reader.Format.Channels)
                .Select(channel => new ChannelAnalyzer(
                    channel,
                    reader.Format.SampleRate,
                    candidates,
                    expectedLengths))
                .ToArray();

            long reportInterval = Math.Max(1, reader.Format.SampleRate / 5);
            progress?.Report(new WavAnalysisProgress(
                $"Scanning {reader.SourceFormat}",
                0,
                0,
                reader.Format.FrameCount,
                0));
            reader.ReadFrames((sample, left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                channels[0].Process(sample, left);
                if (channels.Length == 2)
                {
                    channels[1].Process(sample, right);
                }
                if ((sample % reportInterval) == 0)
                {
                    progress?.Report(new WavAnalysisProgress(
                        $"Scanning {reader.SourceFormat}",
                        reader.Format.FrameCount == 0
                            ? 0
                            : sample / (double)reader.Format.FrameCount,
                        sample,
                        reader.Format.FrameCount,
                        candidates.Count));
                }
            });
            foreach (ChannelAnalyzer channel in channels)
            {
                channel.Flush(reader.Format.FrameCount);
            }

            progress?.Report(new WavAnalysisProgress(
                "Selecting best blocks",
                1,
                reader.Format.FrameCount,
                reader.Format.FrameCount,
                candidates.Count));
            WavAssemblyResult assembly = AssembleRecords(
                candidates,
                reader.Format.SampleRate,
                reader.Format.FrameCount);
            List<SharpBlockCandidate> recoveryCandidates = [];
            if (assembly.Failures.Any(value => value.HeaderFound))
            {
                recoveryCandidates = SelectiveRecovery(
                    reader,
                    assembly.Failures,
                    progress,
                    cancellationToken);
                if (recoveryCandidates.Count > 0)
                {
                    candidates.AddRange(recoveryCandidates);
                    assembly = AssembleRecords(
                        candidates,
                        reader.Format.SampleRate,
                        reader.Format.FrameCount);
                }
            }

            bool selectiveRecoveryUsed = recoveryCandidates.Count > 0;
            var recoveryCandidateSet = recoveryCandidates.ToHashSet();
            IReadOnlyList<WavRecoveryInfo> recordRecoveries = assembly.Recoveries
                .Select(value => new WavRecoveryInfo
                {
                    Header = value.Header,
                    Payload = value.Payload,
                    ReconstructionUsed = value.ReconstructionUsed,
                    FinalProfile = value.FinalProfile,
                    Native1xAnalysis = value.Native1xAnalysis,
                    SelectiveRecoveryUsed = recoveryCandidateSet.Contains(value.Header.Candidate) ||
                        recoveryCandidateSet.Contains(value.Payload.Candidate)
                })
                .ToList();
            IReadOnlyList<WavAnalysisFailure> failures = assembly.Failures
                .Select(value => value with
                {
                    SourceFile = filePath,
                    RecoveryAttempted = selectiveRecoveryUsed && value.HeaderFound
                })
                .ToList();

            progress?.Report(new WavAnalysisProgress(
                "Completed",
                1,
                reader.Format.FrameCount,
                reader.Format.FrameCount,
                candidates.Count));
            return new WavHeuristicAnalysisResult
            {
                Records = assembly.Records,
                Failures = failures,
                Statistics = new WavHeuristicStatistics
                {
                    SourceFile = filePath,
                    Format = reader.Format,
                    SourceFormat = reader.SourceFormat,
                    HeaderCandidates = candidates.Count(value => value.Kind == SharpBlockKind.Header),
                    PayloadCandidates = candidates.Count(value => value.Kind == SharpBlockKind.Payload),
                    ValidPayloadCandidates = candidates.Count(value =>
                        value.Kind == SharpBlockKind.Payload && value.ChecksumValid),
                    ResultRecords = assembly.Records.Count,
                    ReconstructedRecords = assembly.ReconstructedRecords,
                    SelectiveRecoveryUsed = selectiveRecoveryUsed,
                    RecordRecoveries = recordRecoveries,
                    Failures = failures
                }
            };
        }

        private static List<SharpBlockCandidate> SelectiveRecovery(
            IPcmAudioStreamReader reader,
            IReadOnlyList<WavAnalysisFailure> failures,
            IProgress<WavAnalysisProgress>? progress,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<(long Start, long End)> intervals = GetSelectiveRecoveryIntervals(
                failures,
                reader.Format.FrameCount);
            var expectedLengths = failures
                .Select(value => (int?)value.ExpectedLength)
                .Where(value => value is > 0)
                .Select(value => value!.Value)
                .Distinct()
                .ToHashSet();
            var recoveryCandidates = new List<SharpBlockCandidate>();
            foreach ((long firstFrame, long lastFrame) in intervals)
            {
                var channels = Enumerable.Range(0, reader.Format.Channels)
                    .Select(channel => new ChannelAnalyzer(
                        channel,
                        reader.Format.SampleRate,
                        recoveryCandidates,
                        expectedLengths,
                        schmittScale: 0.65))
                    .ToArray();
                progress?.Report(new WavAnalysisProgress(
                    "Selective recovery",
                    firstFrame / (double)reader.Format.FrameCount,
                    firstFrame,
                    reader.Format.FrameCount,
                    recoveryCandidates.Count));
                reader.ReadFrames(firstFrame, lastFrame - firstFrame, (sample, left, right) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    channels[0].Process(sample, left);
                    if (channels.Length == 2)
                    {
                        channels[1].Process(sample, right);
                    }
                });
                foreach (ChannelAnalyzer channel in channels)
                {
                    channel.Flush(lastFrame);
                }
            }
            return recoveryCandidates;
        }

        internal static IReadOnlyList<(long Start, long End)> GetSelectiveRecoveryIntervals(
            IReadOnlyList<WavAnalysisFailure> failures,
            long totalFrames) => failures
                .Where(value => value.HeaderFound)
                .Select(value => (
                    Math.Max(0, value.StartSample),
                    Math.Min(totalFrames, value.EndSample)))
                .Where(value => value.Item2 > value.Item1)
                .OrderBy(value => value.Item1)
                .Aggregate(
                    new List<(long Start, long End)>(),
                    (merged, interval) =>
                    {
                        if (merged.Count == 0 || interval.Item1 > merged[^1].End)
                        {
                            merged.Add(interval);
                        }
                        else
                        {
                            merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.Item2));
                        }
                        return merged;
                    });

        internal static WavAssemblyResult AssembleCandidatesForTest(
            IEnumerable<SharpBlockCandidate> candidates,
            uint sampleRate,
            long totalFrames) =>
            AssembleRecords(candidates.ToList(), sampleRate, totalFrames);

        private static WavAssemblyResult AssembleRecords(
            List<SharpBlockCandidate> candidates,
            uint sampleRate,
            long totalFrames)
        {
            var records = new List<TapeRecord>();
            var recoveries = new List<WavRecoveryInfo>();
            var failures = new List<WavAnalysisFailure>();
            List<SharpBlockCandidate> headers = candidates
                .Where(value => value.Kind == SharpBlockKind.Header && value.ChecksumValid)
                .OrderBy(value => value.EndSample)
                .ToList();
            List<SharpBlockCandidate> payloads = candidates
                .Where(value => value.Kind == SharpBlockKind.Payload)
                .OrderBy(value => value.StartSample)
                .ToList();

            if (headers.Count == 0)
            {
                failures.Add(new WavAnalysisFailure(
                    string.Empty,
                    "No Sharp MZ signal found: no checksum-valid header was detected.",
                    0,
                    totalFrames,
                    null,
                    null,
                    false,
                    payloads.Count > 0,
                    false));
                return new WavAssemblyResult
                {
                    Records = records,
                    Recoveries = recoveries,
                    Failures = failures
                };
            }

            // First collapse candidates that describe the same physical header pulse train.
            // Different channels/detectors can decode the same header either as a generic
            // NORMAL candidate or as a structurally recovered IC/TC/MZ700 candidate.  Do
            // not split those merely because the recovered header bytes differ from the
            // on-tape loader header.  Their end positions are effectively identical.
            long physicalHeaderTolerance = Math.Max(1L, sampleRate / 2L);
            var physicalHeaderGroups = new List<List<SharpBlockCandidate>>();
            foreach (SharpBlockCandidate header in headers)
            {
                List<SharpBlockCandidate>? current = physicalHeaderGroups.LastOrDefault();
                bool samePhysicalHeader = current != null &&
                    header.EndSample - current[^1].EndSample <= physicalHeaderTolerance;
                if (!samePhysicalHeader)
                {
                    current = [];
                    physicalHeaderGroups.Add(current);
                }
                current!.Add(header);
            }

            // Merge duplicate physical header copies without discarding either polarity.
            var groups = new List<List<SharpBlockCandidate>>();
            foreach (List<SharpBlockCandidate> physicalGroup in physicalHeaderGroups)
            {
                List<SharpBlockCandidate>? current = groups.LastOrDefault();
                bool sameRecording = current != null &&
                    physicalGroup.Min(value => value.EndSample) - current.Max(value => value.EndSample) <= sampleRate * 30L &&
                    physicalGroup.Any(header => current.Any(existing =>
                        header.Data.AsSpan().SequenceEqual(existing.Data)));
                if (!sameRecording)
                {
                    current = [];
                    groups.Add(current);
                }
                current!.AddRange(physicalGroup);
            }

            for (int index = 0; index < groups.Count; index++)
            {
                List<SharpBlockCandidate> headerGroup = groups[index];
                long afterHeader = headerGroup.Max(value => value.EndSample);
                long beforeNextHeader = index + 1 < groups.Count
                    ? groups[index + 1].Min(value => value.StartSample)
                    : totalFrames;
                List<ProgramPolarityCandidate> polarityCandidates = new[] { false, true }
                    .Select(inverted => EvaluateProgramPolarity(
                        headerGroup,
                        payloads,
                        inverted,
                        afterHeader,
                        beforeNextHeader))
                    .Where(value => value != null)
                    .Cast<ProgramPolarityCandidate>()
                    .ToList();
                ProgramPolarityCandidate? selected = polarityCandidates
                    .Where(value => value.Payload != null)
                    .OrderByDescending(value => value.PayloadReconstructed ? 1 : 2)
                    .ThenByDescending(value => value.Score)
                    .FirstOrDefault();
                if (selected == null)
                {
                    ProgramPolarityCandidate? attempted = polarityCandidates
                        .OrderByDescending(value => value.Score)
                        .FirstOrDefault();
                    int expectedLength = attempted?.ExpectedLength ??
                        BinaryPrimitives.ReadUInt16LittleEndian(headerGroup[0].Data.AsSpan(18, 2));
                    bool payloadCandidatesFound = polarityCandidates.Any(value => value.PayloadCandidateCount > 0);
                    failures.Add(new WavAnalysisFailure(
                        string.Empty,
                        payloadCandidatesFound
                            ? "Checksum mismatch with no safe recovery: payload copies disagree or remain ambiguous."
                            : "Sharp MZ header found, but no matching payload of the same polarity was detected.",
                        afterHeader,
                        beforeNextHeader,
                        checked((ushort)expectedLength),
                        attempted?.Inverted,
                        true,
                        payloadCandidatesFound,
                        false));
                    continue;
                }

                SharpBlockCandidate header = selected.Header;
                SharpBlockCandidate payload = selected.Payload!;
                TapeProfile finalProfile = header.Profile;
                Native1xTimingAnalysis? native1xAnalysis = null;
                if (header.ProfileEvidence == SharpProfileEvidence.TimingOnly &&
                    header.Profile == TapeProfile.Normal1_1)
                {
                    native1xAnalysis = ClassifyNative1xMachine(
                        candidates,
                        header,
                        payload,
                        sampleRate);
                    finalProfile = native1xAnalysis.Value.ResultProfile;
                }

                byte[] mzf = new byte[TapeRecord.HeaderLength + payload.Data.Length];
                header.Data.CopyTo(mzf, 0);
                payload.Data.CopyTo(mzf, TapeRecord.HeaderLength);
                using var stream = new MemoryStream(mzf, writable: false);
                using var binary = new BinaryReader(stream);
                TapeRecord record = new MZTFileReader().ReadMzfRecord(binary);
                record.Profile = finalProfile;
                record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
                records.Add(record);

                recoveries.Add(new WavRecoveryInfo
                {
                    Header = new WavBlockRecoveryInfo { Candidate = header },
                    Payload = new WavBlockRecoveryInfo { Candidate = payload },
                    ReconstructionUsed = selected.PayloadReconstructed,
                    FinalProfile = finalProfile,
                    Native1xAnalysis = native1xAnalysis
                });
            }

            return new WavAssemblyResult
            {
                Records = records,
                Recoveries = recoveries,
                Failures = failures
            };
        }

        private sealed record ProgramPolarityCandidate(
            bool Inverted,
            SharpBlockCandidate Header,
            SharpBlockCandidate? Payload,
            bool PayloadReconstructed,
            int ExpectedLength,
            int PayloadCandidateCount,
            double Score);

        private static ProgramPolarityCandidate? EvaluateProgramPolarity(
            IReadOnlyList<SharpBlockCandidate> headerGroup,
            IReadOnlyList<SharpBlockCandidate> payloads,
            bool inverted,
            long afterHeader,
            long beforeNextHeader)
        {
            List<SharpBlockCandidate> polarityHeaders = headerGroup
                .Where(value => value.Inverted == inverted)
                .ToList();
            if (polarityHeaders.Count == 0)
            {
                return null;
            }

            SharpBlockCandidate header = SelectBestHeaderCandidate(polarityHeaders);
            int expectedLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Data.AsSpan(18, 2));
            List<SharpBlockCandidate> matchingPayloads = payloads
                .Where(value => value.Inverted == inverted &&
                    value.Data.Length == expectedLength &&
                    value.StartSample >= afterHeader &&
                    value.StartSample < beforeNextHeader)
                .ToList();
            SharpBlockCandidate? payload = matchingPayloads
                .Where(value => value.ChecksumValid)
                .OrderByDescending(value => value.Channel == header.Channel)
                .ThenByDescending(Score)
                .FirstOrDefault();
            bool reconstructed = false;
            if (payload == null)
            {
                payload = TryConsensus(matchingPayloads);
                reconstructed = payload != null;
            }

            return new ProgramPolarityCandidate(
                inverted,
                header,
                payload,
                reconstructed,
                expectedLength,
                matchingPayloads.Count,
                Score(header) + (payload == null ? 0 : Score(payload)));
        }

        private static Native1xTimingAnalysis ClassifyNative1xMachine(
            List<SharpBlockCandidate> allCandidates,
            SharpBlockCandidate selectedHeader,
            SharpBlockCandidate selectedPayload,
            uint sampleRate)
        {
            SharpBlockCandidate header = FindBestTimingCandidate(
                allCandidates,
                selectedHeader,
                sampleRate);
            SharpBlockCandidate payload = FindBestTimingCandidate(
                allCandidates,
                selectedPayload,
                sampleRate);

            SharpTimingMatch header800 = FitCandidateTiming(SharpZ80TimingTable.Rom800Normal, header);
            SharpTimingMatch header700 = FitCandidateTiming(SharpZ80TimingTable.Mz700Normal, header);
            SharpTimingMatch payload800 = FitCandidateTiming(SharpZ80TimingTable.Rom800Normal, payload);
            SharpTimingMatch payload700 = FitCandidateTiming(SharpZ80TimingTable.Mz700Normal, payload);

            double headerRatio = PeriodRatio(header);
            double payloadRatio = PeriodRatio(payload);
            double ratio800 = ReferencePeriodRatio(SharpZ80TimingTable.Rom800Normal);
            double ratio700 = ReferencePeriodRatio(SharpZ80TimingTable.Mz700Normal);

            bool headerRatioVotes700 = RatioVotesMz700(headerRatio, ratio800, ratio700);
            bool payloadRatioVotes700 = RatioVotesMz700(payloadRatio, ratio800, ratio700);
            bool headerFitVotes700 =
                double.IsFinite(header700.RelativeError) && double.IsFinite(header800.RelativeError) &&
                header700.RelativeError + 0.0015 < header800.RelativeError;
            bool payloadFitVotes700 =
                double.IsFinite(payload700.RelativeError) && double.IsFinite(payload800.RelativeError) &&
                payload700.RelativeError + 0.0015 < payload800.RelativeError;

            double combined800 = MeanFinite(header800.RelativeError, payload800.RelativeError);
            double combined700 = MeanFinite(header700.RelativeError, payload700.RelativeError);

            // MZ700 metadata is positive identification, not a fallback. Both the
            // header and payload must independently show the ROM700 period shape,
            // and the full four-half-wave fit must also win. Any disagreement or
            // weak margin remains native MZ-800 NORMAL 1:1.
            bool mz700 =
                headerRatioVotes700 && payloadRatioVotes700 &&
                headerFitVotes700 && payloadFitVotes700 &&
                double.IsFinite(combined700) && double.IsFinite(combined800) &&
                combined700 + 0.0020 < combined800;

            return new Native1xTimingAnalysis(
                mz700 ? TapeProfile.Mz700_1_1 : TapeProfile.Normal1_1,
                headerRatio,
                payloadRatio,
                header800.RelativeError,
                header700.RelativeError,
                payload800.RelativeError,
                payload700.RelativeError,
                combined800,
                combined700);
        }

        private static SharpBlockCandidate FindBestTimingCandidate(
            List<SharpBlockCandidate> candidates,
            SharpBlockCandidate selected,
            uint sampleRate)
        {
            long tolerance = Math.Max(1L, sampleRate / 2L);
            IEnumerable<SharpBlockCandidate> matching = candidates.Where(candidate =>
                candidate.Kind == selected.Kind &&
                candidate.Inverted == selected.Inverted &&
                candidate.ChecksumValid &&
                candidate.Data.Length == selected.Data.Length &&
                candidate.Data.AsSpan().SequenceEqual(selected.Data) &&
                Math.Abs(candidate.EndSample - selected.EndSample) <= tolerance);

            List<SharpBlockCandidate> zeroCrossing = matching
                .Where(candidate => candidate.PulseMode == WavPulseMode.ZeroCrossing && HasCompleteTiming(candidate))
                .OrderBy(candidate => Math.Min(
                    FitCandidateTiming(SharpZ80TimingTable.Rom800Normal, candidate).RelativeError,
                    FitCandidateTiming(SharpZ80TimingTable.Mz700Normal, candidate).RelativeError))
                .ThenByDescending(Score)
                .ToList();
            if (zeroCrossing.Count != 0)
            {
                return zeroCrossing[0];
            }

            return matching
                .Where(HasCompleteTiming)
                .OrderBy(candidate => Math.Min(
                    FitCandidateTiming(SharpZ80TimingTable.Rom800Normal, candidate).RelativeError,
                    FitCandidateTiming(SharpZ80TimingTable.Mz700Normal, candidate).RelativeError))
                .ThenByDescending(Score)
                .FirstOrDefault() ?? selected;
        }

        private static bool HasCompleteTiming(SharpBlockCandidate candidate) =>
            double.IsFinite(candidate.TimingShortHighMicroseconds) && candidate.TimingShortHighMicroseconds > 0 &&
            double.IsFinite(candidate.TimingShortLowMicroseconds) && candidate.TimingShortLowMicroseconds > 0 &&
            double.IsFinite(candidate.TimingLongHighMicroseconds) && candidate.TimingLongHighMicroseconds > 0 &&
            double.IsFinite(candidate.TimingLongLowMicroseconds) && candidate.TimingLongLowMicroseconds > 0;

        private static SharpTimingMatch FitCandidateTiming(
            SharpZ80TimingReference reference,
            SharpBlockCandidate candidate) =>
            SharpTapeCandidateScanner.FitTiming(
                reference,
                candidate.TimingShortHighMicroseconds,
                candidate.TimingShortLowMicroseconds,
                candidate.TimingLongHighMicroseconds,
                candidate.TimingLongLowMicroseconds);

        private static double PeriodRatio(SharpBlockCandidate candidate)
        {
            if (!HasCompleteTiming(candidate))
            {
                return double.NaN;
            }
            double shortPeriod = candidate.TimingShortHighMicroseconds + candidate.TimingShortLowMicroseconds;
            double longPeriod = candidate.TimingLongHighMicroseconds + candidate.TimingLongLowMicroseconds;
            return shortPeriod > 0 ? longPeriod / shortPeriod : double.NaN;
        }

        private static double ReferencePeriodRatio(SharpZ80TimingReference reference) =>
            (reference.LongHighMicroseconds + reference.LongLowMicroseconds) /
            (reference.ShortHighMicroseconds + reference.ShortLowMicroseconds);

        private static bool RatioVotesMz700(double measured, double mz800, double mz700)
        {
            if (!double.IsFinite(measured))
            {
                return false;
            }
            double error800 = Math.Abs(measured - mz800);
            double error700 = Math.Abs(measured - mz700);
            return error700 + 0.0015 < error800;
        }

        private static double MeanFinite(double first, double second)
        {
            bool firstFinite = double.IsFinite(first);
            bool secondFinite = double.IsFinite(second);
            if (firstFinite && secondFinite)
            {
                return (first + second) / 2.0;
            }
            if (firstFinite)
            {
                return first;
            }
            if (secondFinite)
            {
                return second;
            }
            return double.PositiveInfinity;
        }

        private static SharpBlockCandidate SelectBestHeaderCandidate(
            IEnumerable<SharpBlockCandidate> candidates)
        {
            List<SharpBlockCandidate> candidateList = candidates.ToList();
            int strongestEvidence = candidateList
                .Max(candidate => ProfileEvidenceRank(candidate.ProfileEvidence));
            List<SharpBlockCandidate> strongest = candidateList
                .Where(candidate => ProfileEvidenceRank(candidate.ProfileEvidence) == strongestEvidence)
                .ToList();

            if (strongestEvidence == ProfileEvidenceRank(SharpProfileEvidence.TimingOnly))
            {
                // Zero-crossing preserves edge timing better than an amplitude
                // Schmitt threshold. Use it first for metadata classification, then
                // choose the candidate that best fits the ROM/Z80 four-half-wave
                // timing table after removal of one common tape-speed scale factor.
                List<SharpBlockCandidate> zeroCrossing = strongest
                    .Where(candidate => candidate.PulseMode == WavPulseMode.ZeroCrossing)
                    .ToList();
                IEnumerable<SharpBlockCandidate> timingPool = zeroCrossing.Count != 0
                    ? zeroCrossing
                    : strongest;
                return timingPool
                    .OrderBy(candidate => candidate.TimingError)
                    .ThenByDescending(Score)
                    .First();
            }

            return strongest
                .OrderByDescending(Score)
                .First();
        }

        private static int ProfileEvidenceRank(SharpProfileEvidence evidence) => evidence switch
        {
            SharpProfileEvidence.TurboCopyHeaderAndLoader => 4,
            SharpProfileEvidence.IntercopyHeader => 3,
            SharpProfileEvidence.StructuredMz700 => 2,
            _ => 1
        };

        private static double Score(SharpBlockCandidate candidate)
        {
            double score = candidate.ChecksumValid ? 1_000_000 : 0;
            score += candidate.PulseConfidence * 1000;

            // PolarityConfidence stores the signed HIGH-LOW duration difference,
            // not a normalized probability. Its magnitude must not reward a
            // longer HIGH half-wave, because that would bias the 1:1 machine
            // tie-break toward MZ700. Only the sign is useful here.
            if (candidate.PolarityConfidence > 0)
            {
                score += 5;
            }
            else if (candidate.PolarityConfidence < 0)
            {
                score -= 5;
            }
            score -= candidate.LeaderStdDev;
            if (candidate.PulseMode == WavPulseMode.ZeroCrossing)
            {
                score += 2;
            }
            if (!candidate.Inverted)
            {
                score += 1;
            }
            return score;
        }

        private static SharpBlockCandidate? TryConsensus(List<SharpBlockCandidate> candidates)
        {
            if (candidates.Count < 2)
            {
                return null;
            }
            var checksumGroup = candidates
                .GroupBy(value => value.RecordedChecksum)
                .OrderByDescending(group => group.Count())
                .First();
            if (checksumGroup.Count() < 2)
            {
                return null;
            }

            int length = candidates[0].Data.Length;
            byte[] data = new byte[length];
            for (int byteIndex = 0; byteIndex < length; byteIndex++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    int mask = 1 << bit;
                    double balance = 0;
                    double weight = 0;
                    foreach (SharpBlockCandidate candidate in candidates)
                    {
                        double confidence = Math.Max(0.05, candidate.PulseConfidence);
                        weight += confidence;
                        balance += (candidate.Data[byteIndex] & mask) != 0 ? confidence : -confidence;
                    }
                    if (Math.Abs(balance) < weight * 0.25)
                    {
                        return null;
                    }
                    if (balance > 0)
                    {
                        data[byteIndex] |= (byte)mask;
                    }
                }
            }

            ushort calculated = CalculateChecksum(data);
            if (calculated != checksumGroup.Key)
            {
                return null;
            }
            SharpBlockCandidate best = candidates.OrderByDescending(Score).First();
            return new SharpBlockCandidate
            {
                Kind = SharpBlockKind.Payload,
                Channel = best.Channel,
                CopyIndex = best.CopyIndex,
                Inverted = best.Inverted,
                PulseMode = best.PulseMode,
                Data = data,
                ChecksumValid = true,
                RecordedChecksum = checksumGroup.Key,
                CalculatedChecksum = calculated,
                StartSample = candidates.Min(value => value.StartSample),
                EndSample = candidates.Max(value => value.EndSample),
                LeaderAverage = candidates.Average(value => value.LeaderAverage),
                LeaderStdDev = candidates.Average(value => value.LeaderStdDev),
                PulseConfidence = candidates.Average(value => value.PulseConfidence)
            };
        }

        private static ushort CalculateChecksum(ReadOnlySpan<byte> data)
        {
            uint checksum = 0;
            foreach (byte value in data)
            {
                checksum += (uint)BitOperations.PopCount(value);
            }
            return unchecked((ushort)checksum);
        }

        private sealed class ChannelAnalyzer
        {
            private readonly SignalPreprocessor preprocessor;
            private readonly PulseDetector zeroCrossing;
            private readonly PulseDetector schmitt;
            private readonly SharpTapeCandidateScanner zeroNormal;
            private readonly SharpTapeCandidateScanner zeroInverted;
            private readonly SharpTapeCandidateScanner schmittNormal;
            private readonly SharpTapeCandidateScanner schmittInverted;
            private readonly double schmittScale;

            internal ChannelAnalyzer(
                int channel,
                uint sampleRate,
                List<SharpBlockCandidate> output,
                HashSet<int> expectedLengths,
                double schmittScale = 1.0)
            {
                this.schmittScale = schmittScale;
                preprocessor = new SignalPreprocessor(sampleRate);
                zeroNormal = new SharpTapeCandidateScanner(channel, false, WavPulseMode.ZeroCrossing, sampleRate, output, expectedLengths);
                zeroInverted = new SharpTapeCandidateScanner(channel, true, WavPulseMode.ZeroCrossing, sampleRate, output, expectedLengths);
                schmittNormal = new SharpTapeCandidateScanner(channel, false, WavPulseMode.Schmitt, sampleRate, output, expectedLengths);
                schmittInverted = new SharpTapeCandidateScanner(channel, true, WavPulseMode.Schmitt, sampleRate, output, expectedLengths);
                zeroCrossing = new PulseDetector((level, duration, end, confidence) =>
                {
                    zeroNormal.Feed(duration, level, end, confidence);
                    zeroInverted.Feed(duration, !level, end, confidence);
                });
                schmitt = new PulseDetector((level, duration, end, confidence) =>
                {
                    schmittNormal.Feed(duration, level, end, confidence);
                    schmittInverted.Feed(duration, !level, end, confidence);
                });
                foreach (int length in expectedLengths)
                {
                    zeroNormal.AddExpectedBlockLength(length);
                    zeroInverted.AddExpectedBlockLength(length);
                    schmittNormal.AddExpectedBlockLength(length);
                    schmittInverted.AddExpectedBlockLength(length);
                }
            }

            internal void Process(long sampleIndex, int pcm)
            {
                (double filtered, double threshold) = preprocessor.Process(pcm / 8388608.0);
                zeroCrossing.Process(sampleIndex, filtered >= 0);
                schmitt.ProcessSchmitt(sampleIndex, filtered, threshold * schmittScale);
            }

            internal void Flush(long endSample)
            {
                zeroCrossing.Flush(endSample);
                schmitt.Flush(endSample);
            }
        }

        private sealed class SignalPreprocessor
        {
            private readonly double dcAlpha;
            private readonly double highPassAlpha;
            private readonly double envelopeAttack;
            private readonly double envelopeRelease;
            private double dc;
            private double previousCentered;
            private double highPassed;
            private double envelope;

            internal SignalPreprocessor(uint sampleRate)
            {
                dcAlpha = 1.0 - Math.Exp(-1.0 / (sampleRate * 0.75));
                highPassAlpha = Math.Exp(-2.0 * Math.PI * 20.0 / sampleRate);
                envelopeAttack = 1.0 - Math.Exp(-1.0 / (sampleRate * 0.005));
                envelopeRelease = 1.0 - Math.Exp(-1.0 / (sampleRate * 0.250));
            }

            internal (double Filtered, double Threshold) Process(double sample)
            {
                dc += (sample - dc) * dcAlpha;
                double centered = sample - dc;
                highPassed = highPassAlpha * (highPassed + centered - previousCentered);
                previousCentered = centered;
                double magnitude = Math.Abs(highPassed);
                envelope += (magnitude - envelope) *
                    (magnitude > envelope ? envelopeAttack : envelopeRelease);
                return (highPassed, Math.Max(0.0025, envelope * 0.22));
            }
        }

        private sealed class PulseDetector
        {
            private readonly Action<bool, long, long, double> emit;
            private readonly WavPulseHistogram histogram = new();
            private bool? level;
            private long edgeSample;

            internal PulseDetector(Action<bool, long, long, double> emit) => this.emit = emit;

            internal void Process(long sampleIndex, bool current)
            {
                if (level == current)
                {
                    return;
                }
                if (level.HasValue && sampleIndex > edgeSample)
                {
                    long duration = sampleIndex - edgeSample;
                    histogram.Observe(duration, level.Value);
                    emit(level.Value, duration, sampleIndex, histogram.Confidence);
                }
                level = current;
                edgeSample = sampleIndex;
            }

            internal void ProcessSchmitt(long sampleIndex, double sample, double threshold)
            {
                bool current = level ?? sample >= 0;
                if (!current && sample >= threshold)
                {
                    current = true;
                }
                else if (current && sample <= -threshold)
                {
                    current = false;
                }
                Process(sampleIndex, current);
            }

            internal void Flush(long endSample)
            {
                if (level.HasValue && endSample > edgeSample)
                {
                    long duration = endSample - edgeSample;
                    histogram.Observe(duration, level.Value);
                    emit(level.Value, duration, endSample, histogram.Confidence);
                    edgeSample = endSample;
                }
            }
        }

        private sealed class WavPulseHistogram
        {
            private const int MaximumTrackedSamples = 2048;
            private readonly int[] high = new int[MaximumTrackedSamples + 1];
            private readonly int[] low = new int[MaximumTrackedSamples + 1];
            private int observations;

            internal double Confidence { get; private set; } = 0.5;

            internal void Observe(long duration, bool physicalHigh)
            {
                if (duration is <= 0 or > MaximumTrackedSamples)
                {
                    return;
                }
                int[] bins = physicalHigh ? high : low;
                if (bins[duration] < int.MaxValue)
                {
                    bins[duration]++;
                }
                observations++;
                if ((observations & 255) == 0)
                {
                    Confidence = CalculateClusterConfidence(high, low);
                }
            }

            private static double CalculateClusterConfidence(int[] high, int[] low)
            {
                int first = 0;
                int firstCount = 0;
                for (int index = 1; index < high.Length; index++)
                {
                    int count = high[index] + low[index];
                    if (count > firstCount)
                    {
                        first = index;
                        firstCount = count;
                    }
                }
                if (first == 0 || firstCount == 0)
                {
                    return 0.25;
                }

                int secondCount = 0;
                int minimumSecond = Math.Min(high.Length - 1, (first * 4 + 2) / 3);
                for (int index = minimumSecond; index < high.Length; index++)
                {
                    secondCount = Math.Max(secondCount, high[index] + low[index]);
                }
                double support = Math.Min(1.0, (firstCount + secondCount) / 64.0);
                double separation = secondCount == 0 ? 0.25 : 1.0;
                return 0.25 + (0.75 * support * separation);
            }
        }
    }

    internal sealed class SharpTapeCandidateScanner
    {
        private readonly int channel;
        private readonly bool inverted;
        private readonly WavPulseMode pulseMode;
        private readonly uint sampleRate;
        private readonly List<SharpBlockCandidate> output;
        private readonly HashSet<int> expectedLengths;
        private readonly SharpMzPulseDecoder headerDecoder = new();
        private readonly Dictionary<int, RawBlockScanner> blocks = [];
        private readonly List<(
            byte[] Header,
            double PolarityConfidence,
            SharpMzDecoderEvent DecoderEvent,
            long EndSample,
            SharpTimingMatch TimingMatch)> turboCopyHeaders = [];
        private double pulseConfidence = 0.5;

        internal SharpTapeCandidateScanner(
            int channel,
            bool inverted,
            WavPulseMode pulseMode,
            uint sampleRate,
            List<SharpBlockCandidate> output,
            HashSet<int> expectedLengths)
        {
            this.channel = channel;
            this.inverted = inverted;
            this.pulseMode = pulseMode;
            this.sampleRate = sampleRate;
            this.output = output;
            this.expectedLengths = expectedLengths;
            headerDecoder.BeginHeader();
        }

        internal void Feed(
            long durationSamples,
            bool physicalHigh,
            long endSample,
            double confidence)
        {
            pulseConfidence = confidence;
            foreach (int byteCount in expectedLengths)
            {
                EnsureBlockScanner(byteCount);
            }

            // Snapshot the scanners that were already active before this interval.
            // A scanner created while processing HeaderValid must start with the NEXT
            // interval, exactly like the standard DecodeRecords() path calls StartData()
            // only after the header-completing pulse has already been consumed.
            RawBlockScanner[] activeBlocks = blocks.Values.ToArray();

            headerDecoder.FeedInterval(durationSamples, physicalHigh);
            while (headerDecoder.TryTakeEvent(out SharpMzDecoderEvent decoderEvent))
            {
                if (decoderEvent.Type == SharpMzDecoderEventType.HeaderValid)
                {
                    AcceptHeader(decoderEvent, endSample);
                    headerDecoder.BeginHeader();
                }
                else if (decoderEvent.Type == SharpMzDecoderEventType.HeaderInvalid)
                {
                    // Discovery must continue after every candidate instead of
                    // committing to the decoder's conventional duplicate-copy path.
                    headerDecoder.BeginHeader();
                }
            }

            foreach (RawBlockScanner block in activeBlocks)
            {
                SharpBlockCandidate? candidate = block.Feed(
                    durationSamples,
                    physicalHigh,
                    endSample,
                    pulseConfidence);
                if (candidate == null)
                {
                    continue;
                }
                output.Add(candidate);
                if (candidate.ChecksumValid && candidate.Data.Length == 90 &&
                    SharpTapeProfileEncoder.IsTurboCopyLoader(candidate.Data))
                {
                    List<(byte[] Header, double PolarityConfidence, SharpMzDecoderEvent DecoderEvent, long EndSample, SharpTimingMatch TimingMatch)>
                        matchingHeaders = turboCopyHeaders
                            .Where(value =>
                                value.EndSample <= candidate.StartSample &&
                                candidate.EndSample - value.EndSample <= sampleRate * 30L)
                            .OrderByDescending(value => value.EndSample)
                            .ToList();

                    if (matchingHeaders.Count > 0)
                    {
                        var pending = matchingHeaders[0];
                        byte[] recovered = SharpTapeImporter.RecoverTurboCopyHeader(
                            pending.Header,
                            candidate.Data,
                            out TapeProfile profile);
                        SharpTimingMatch tcHeaderFit = FitTiming(
                            SharpZ80TimingTable.Rom800Normal,
                            pending.TimingMatch.ShortHighMicroseconds,
                            pending.TimingMatch.ShortLowMicroseconds,
                            pending.TimingMatch.LongHighMicroseconds,
                            pending.TimingMatch.LongLowMicroseconds);
                        AddHeaderCandidate(
                            recovered,
                            profile,
                            pending.DecoderEvent,
                            pending.EndSample,
                            pending.PolarityConfidence,
                            SharpProfileEvidence.TurboCopyHeaderAndLoader,
                            tcHeaderFit.TapeSpeedScale,
                            tcHeaderFit.RelativeError,
                            tcHeaderFit.ShortHighMicroseconds,
                            tcHeaderFit.ShortLowMicroseconds,
                            tcHeaderFit.LongHighMicroseconds,
                            tcHeaderFit.LongLowMicroseconds);
                        AddExpectedBlockLength(
                            BinaryPrimitives.ReadUInt16LittleEndian(recovered.AsSpan(18, 2)));

                        turboCopyHeaders.RemoveAll(value =>
                            value.EndSample <= candidate.StartSample &&
                            value.Header.AsSpan().SequenceEqual(pending.Header));
                    }
                }
            }
        }

        internal void AddExpectedBlockLength(int byteCount)
        {
            if (byteCount <= 0)
            {
                return;
            }
            expectedLengths.Add(byteCount);
            EnsureBlockScanner(byteCount);
        }

        private void AcceptHeader(SharpMzDecoderEvent decoderEvent, long endSample)
        {
            byte[] encoded = headerDecoder.ValidatedHeader!;
            TapeProfile coarseProfile = SharpTapeImporter.ProfileFromWavTone(
                headerDecoder.HeaderShortPhysicalLowX8,
                headerDecoder.HeaderShortPhysicalHighX8,
                decoderEvent.LeaderPulses,
                sampleRate);
            SharpTimingMatch timingMatch = ClassifyHeaderTiming(
                headerDecoder.HeaderLeaderPhysicalLowMeanX8 > 0
                    ? headerDecoder.HeaderLeaderPhysicalLowMeanX8
                    : headerDecoder.HeaderShortPhysicalLowX8,
                headerDecoder.HeaderShortPhysicalHighX8,
                headerDecoder.HeaderLongPhysicalLowX8,
                headerDecoder.HeaderLongPhysicalHighX8,
                sampleRate,
                coarseProfile);

            if (SharpTapeImporter.IsTurboCopyHeader(encoded))
            {
                turboCopyHeaders.Add((
                    (byte[])encoded.Clone(),
                    headerDecoder.HeaderShortPhysicalHighX8 -
                        headerDecoder.HeaderShortPhysicalLowX8,
                    decoderEvent,
                    endSample,
                    timingMatch));
                AddExpectedBlockLength(90);
                return;
            }

            byte[] header = encoded;
            SharpProfileEvidence profileEvidence = SharpProfileEvidence.TimingOnly;
            TapeProfile profile = timingMatch.Profile;
            if (SharpTapeImporter.TryRecoverIntercopyHeader(encoded, out byte[]? recoveredIc, out TapeProfile icProfile))
            {
                header = recoveredIc!;
                profile = icProfile;
                profileEvidence = SharpProfileEvidence.IntercopyHeader;
            }
            else if (SharpTapeImporter.TryRecoverMz700FastHeader(encoded, out byte[]? recoveredMz700))
            {
                header = recoveredMz700!;
                profile = TapeProfile.Mz700_1_3;
                profileEvidence = SharpProfileEvidence.StructuredMz700;
            }
            else if (header[0] == 0 || (header[17] != 0x0D && header[17] != 0x00))
            {
                return;
            }

            AddHeaderCandidate(
                header,
                profile,
                decoderEvent,
                endSample,
                profileEvidence: profileEvidence,
                timingScale: timingMatch.TapeSpeedScale,
                timingError: timingMatch.RelativeError,
                timingShortHighMicroseconds: timingMatch.ShortHighMicroseconds,
                timingShortLowMicroseconds: timingMatch.ShortLowMicroseconds,
                timingLongHighMicroseconds: timingMatch.LongHighMicroseconds,
                timingLongLowMicroseconds: timingMatch.LongLowMicroseconds);
            AddExpectedBlockLength(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2)));
        }

        private static SharpTimingMatch ClassifyHeaderTiming(
            long shortPhysicalLowX8,
            long shortPhysicalHighX8,
            long longPhysicalLowX8,
            long longPhysicalHighX8,
            uint sampleRate,
            TapeProfile coarseProfile)
        {
            if (sampleRate == 0 || shortPhysicalLowX8 <= 0 || shortPhysicalHighX8 <= 0)
            {
                return new SharpTimingMatch(
                    TapeProfile.Normal1_1,
                    1.0,
                    double.PositiveInfinity,
                    double.NaN,
                    double.NaN,
                    double.NaN,
                    double.NaN);
            }

            // Connector inversion in QDTool means physical LOW is logical HIGH
            // and physical HIGH is logical LOW. Convert all four measured header
            // half-waves to microseconds before fitting the Z80 reference table.
            double shortHigh = X8SamplesToMicroseconds(shortPhysicalLowX8, sampleRate);
            double shortLow = X8SamplesToMicroseconds(shortPhysicalHighX8, sampleRate);
            double longHigh = longPhysicalLowX8 > 0
                ? X8SamplesToMicroseconds(longPhysicalLowX8, sampleRate)
                : double.NaN;
            double longLow = longPhysicalHighX8 > 0
                ? X8SamplesToMicroseconds(longPhysicalHighX8, sampleRate)
                : double.NaN;

            List<SharpTimingMatch> matches = SharpZ80TimingTable.HeaderTimingCandidates
                .Select(reference => FitTiming(reference, shortHigh, shortLow, longHigh, longLow))
                .OrderBy(match => match.RelativeError)
                .ToList();

            // Keep the already proven QDTool speed classifier for 1:2/1:3/1:4.
            // The four-half-wave ROM/Z80 fit is used as a shape/quality check and
            // specifically resolves the only ambiguous machine family: 1:1.
            if (coarseProfile is TapeProfile.Normal1_2 or TapeProfile.Normal1_3 or TapeProfile.Normal1_4)
            {
                return matches.First(match => match.Profile == coarseProfile);
            }

            SharpTimingMatch mz800 = matches.First(match => match.Profile == TapeProfile.Normal1_1);
            SharpTimingMatch mz700 = matches.First(match => match.Profile == TapeProfile.Mz700_1_1);
            SharpTimingMatch bestNative = mz800.RelativeError <= mz700.RelativeError
                ? mz800
                : mz700;

            // Do not decide MZ700 vs MZ-800 from one header.  IC 1:1 and TC 1:1
            // also carry an MZ-800 ROM-timed header, and a noisy cassette can move
            // individual half-wave edges.  Native 1:1 machine identification is
            // deliberately postponed until AssembleRecords(), where both the
            // checksum-valid header and payload must agree.
            return new SharpTimingMatch(
                TapeProfile.Normal1_1,
                bestNative.TapeSpeedScale,
                bestNative.RelativeError,
                bestNative.ShortHighMicroseconds,
                bestNative.ShortLowMicroseconds,
                bestNative.LongHighMicroseconds,
                bestNative.LongLowMicroseconds);
        }

        internal static SharpTimingMatch FitTiming(
            SharpZ80TimingReference reference,
            double shortHigh,
            double shortLow,
            double longHigh,
            double longLow)
        {
            double[] measured = [shortHigh, shortLow, longHigh, longLow];
            double[] expected =
            [
                reference.ShortHighMicroseconds,
                reference.ShortLowMicroseconds,
                reference.LongHighMicroseconds,
                reference.LongLowMicroseconds
            ];

            double logScaleSum = 0;
            int count = 0;
            for (int index = 0; index < measured.Length; index++)
            {
                if (double.IsFinite(measured[index]) && measured[index] > 0 &&
                    double.IsFinite(expected[index]) && expected[index] > 0)
                {
                    logScaleSum += Math.Log(measured[index] / expected[index]);
                    count++;
                }
            }
            if (count < 2)
            {
                return new SharpTimingMatch(
                    reference.Profile,
                    1.0,
                    double.PositiveInfinity,
                    shortHigh,
                    shortLow,
                    longHigh,
                    longLow);
            }

            double logScale = logScaleSum / count;
            double scale = Math.Exp(logScale);
            double squareError = 0;
            for (int index = 0; index < measured.Length; index++)
            {
                if (double.IsFinite(measured[index]) && measured[index] > 0 &&
                    double.IsFinite(expected[index]) && expected[index] > 0)
                {
                    double residual = Math.Log(measured[index] / expected[index]) - logScale;
                    squareError += residual * residual;
                }
            }
            return new SharpTimingMatch(
                reference.Profile,
                scale,
                Math.Sqrt(squareError / count),
                shortHigh,
                shortLow,
                longHigh,
                longLow);
        }

        private static double X8SamplesToMicroseconds(long valueX8, uint sampleRate) =>
            valueX8 * 125000.0 / sampleRate;

        private void AddHeaderCandidate(
            byte[] header,
            TapeProfile profile,
            SharpMzDecoderEvent decoderEvent,
            long endSample,
            double? polarityConfidence = null,
            SharpProfileEvidence profileEvidence = SharpProfileEvidence.TimingOnly,
            double timingScale = 1.0,
            double timingError = double.PositiveInfinity,
            double timingShortHighMicroseconds = double.NaN,
            double timingShortLowMicroseconds = double.NaN,
            double timingLongHighMicroseconds = double.NaN,
            double timingLongLowMicroseconds = double.NaN)
        {
            output.Add(new SharpBlockCandidate
            {
                Kind = SharpBlockKind.Header,
                Channel = channel,
                CopyIndex = decoderEvent.CopyIndex,
                Inverted = inverted,
                PulseMode = pulseMode,
                Data = (byte[])header.Clone(),
                ChecksumValid = true,
                RecordedChecksum = decoderEvent.RecordedChecksum,
                CalculatedChecksum = decoderEvent.CalculatedChecksum,
                StartSample = endSample,
                EndSample = endSample,
                LeaderAverage = headerDecoder.LeaderAverage,
                LeaderStdDev = headerDecoder.LeaderStdDev,
                PulseConfidence = pulseConfidence * headerDecoder.PulseConfidence *
                    Math.Min(1.0, Math.Max(0.25, decoderEvent.LeaderPulses / 5500.0)),
                PolarityConfidence = polarityConfidence ??
                    (headerDecoder.HeaderShortPhysicalHighX8 -
                        headerDecoder.HeaderShortPhysicalLowX8),
                TimingScale = timingScale,
                TimingError = timingError,
                TimingShortHighMicroseconds = timingShortHighMicroseconds,
                TimingShortLowMicroseconds = timingShortLowMicroseconds,
                TimingLongHighMicroseconds = timingLongHighMicroseconds,
                TimingLongLowMicroseconds = timingLongLowMicroseconds,
                Profile = profile,
                ProfileEvidence = profileEvidence
            });
        }

        private void EnsureBlockScanner(int byteCount)
        {
            if (byteCount <= 0 || blocks.ContainsKey(byteCount))
            {
                return;
            }
            blocks.Add(byteCount, new RawBlockScanner(
                byteCount,
                channel,
                inverted,
                pulseMode,
                sampleRate));
        }

        private sealed class RawBlockScanner
        {
            private readonly int byteCount;
            private readonly int channel;
            private readonly bool inverted;
            private readonly WavPulseMode pulseMode;
            private readonly uint sampleRate;
            private readonly SharpMzPulseDecoder decoder = new();
            private readonly List<byte> data;
            private long startSample;
            private int copyIndex;

            internal RawBlockScanner(
                int byteCount,
                int channel,
                bool inverted,
                WavPulseMode pulseMode,
                uint sampleRate)
            {
                this.byteCount = byteCount;
                this.channel = channel;
                this.inverted = inverted;
                this.pulseMode = pulseMode;
                this.sampleRate = sampleRate;
                data = new List<byte>(byteCount);
                decoder.BeginRawBlock(byteCount);
            }

            internal SharpBlockCandidate? Feed(
                long duration,
                bool physicalHigh,
                long endSample,
                double pulseConfidence)
            {
                decoder.FeedInterval(duration, physicalHigh);
                while (decoder.TryTakeEvent(out SharpMzDecoderEvent decoderEvent))
                {
                    if (decoderEvent.Type == SharpMzDecoderEventType.DataByte)
                    {
                        if (decoderEvent.ByteIndex == 0)
                        {
                            data.Clear();
                            startSample = endSample;
                        }
                        if (decoderEvent.ByteIndex == data.Count)
                        {
                            data.Add(decoderEvent.Value);
                        }
                        continue;
                    }
                    if (decoderEvent.Type is not SharpMzDecoderEventType.BlockValid and
                        not SharpMzDecoderEventType.BlockInvalid)
                    {
                        continue;
                    }

                    byte[] bytes = data.Count == byteCount ? data.ToArray() : new byte[byteCount];
                    double shortHigh = decoder.CompletedLeaderPhysicalLowMeanX8 > 0
                        ? X8SamplesToMicroseconds(decoder.CompletedLeaderPhysicalLowMeanX8, sampleRate)
                        : double.NaN;
                    double shortLow = decoder.CompletedShortPhysicalHighX8 > 0
                        ? X8SamplesToMicroseconds(decoder.CompletedShortPhysicalHighX8, sampleRate)
                        : double.NaN;
                    double longHigh = decoder.CompletedLongPhysicalLowX8 > 0
                        ? X8SamplesToMicroseconds(decoder.CompletedLongPhysicalLowX8, sampleRate)
                        : double.NaN;
                    double longLow = decoder.CompletedLongPhysicalHighX8 > 0
                        ? X8SamplesToMicroseconds(decoder.CompletedLongPhysicalHighX8, sampleRate)
                        : double.NaN;

                    var candidate = new SharpBlockCandidate
                    {
                        Kind = SharpBlockKind.Payload,
                        Channel = channel,
                        CopyIndex = copyIndex++,
                        Inverted = inverted,
                        PulseMode = pulseMode,
                        Data = bytes,
                        ChecksumValid = decoderEvent.Type == SharpMzDecoderEventType.BlockValid,
                        RecordedChecksum = decoderEvent.RecordedChecksum,
                        CalculatedChecksum = decoderEvent.CalculatedChecksum,
                        StartSample = startSample,
                        EndSample = endSample,
                        LeaderAverage = decoder.LeaderAverage,
                        LeaderStdDev = decoder.LeaderStdDev,
                        PulseConfidence = pulseConfidence * decoder.PulseConfidence *
                            (decoderEvent.Type == SharpMzDecoderEventType.BlockValid ? 1.0 : 0.45),
                        TimingShortHighMicroseconds = shortHigh,
                        TimingShortLowMicroseconds = shortLow,
                        TimingLongHighMicroseconds = longHigh,
                        TimingLongLowMicroseconds = longLow
                    };
                    data.Clear();
                    decoder.BeginRawBlock(byteCount);
                    return candidate;
                }
                return null;
            }
        }
    }
}
