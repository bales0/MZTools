using System;
using System.Numerics;

namespace MZTools
{
    internal sealed record SharpBlockSignalTrace(long LeaderStart, long SyncStart, long DataStart, long ChecksumStart, long End)
    {
        internal SharpBlockSignalTrace Shift(long delta) => new(LeaderStart + delta, SyncStart + delta, DataStart + delta, ChecksumStart + delta, End + delta);
        internal bool IsOrdered => LeaderStart >= 0 && LeaderStart <= SyncStart && SyncStart <= DataStart && DataStart <= ChecksumStart && ChecksumStart <= End;
    }
    internal enum SharpMzDecoderEventType
    {
        None,
        HeaderValid,
        HeaderInvalid,
        DataByte,
        BlockValid,
        BlockInvalid
    }

    internal readonly record struct SharpMzDecoderEvent(
        SharpMzDecoderEventType Type,
        byte Value,
        int ByteIndex,
        ushort CalculatedChecksum,
        ushort RecordedChecksum,
        int LeaderPulses,
        int CopyIndex)
    {
        internal SharpBlockSignalTrace? SignalTrace { get; init; }
    }

    // Behavioral port of MZ-SD2CMT2-Reborn/src/formats/mz_tape_decoder.cpp.
    // Physical HIGH intervals are retained by the importer, but only physical
    // LOW intervals reach the Sharp pulse classifier.
    internal sealed class SharpMzPulseDecoder
    {
        private const int HeaderBytes = 128;
        private const int MinLeaderPulses = 16;
        private const int MinMarkPulses = 12;
        private const int MaxMarkPulses = 48;
        private const int FinalMarkPulses = 2;
        private const int MaxHalfUnits = 1024;
        private const int LeaderLockPulses = 256;
        private readonly double? fixedShortUnits;
        private readonly double? fuzzyTolerance;
        private readonly ReferencePulseRules? reference;
        private readonly int unitsPerSample;
        private readonly bool halfSample;
        private int previousReferenceClass = -1;
        private readonly Action<SharpPulseDiagnostic>? diagnostic;
        private readonly double[] recentPulses = new double[6];
        private int recentPulseIndex;
        private bool unknownLength;
        private byte penultimateByte, lastByte;
        private long beforeLastByteEnd, lastByteEnd, unknownDataEnd;
        internal long Position => ToSamples(intervalEndUnits);
        private void Diagnose(string code, long duration = 0) => diagnostic?.Invoke(new(code, state.ToString(), Position, byteIndex,
            unknownLength ? null : expectedBytes, duration / (double)unitsPerSample,
            "Recent half-periods (samples): " + string.Join(", ", recentPulses)));

        // Opt-in manual recovery settings; the default decoder retains its original rules.
        internal SharpMzPulseDecoder(double? fixedShortUnits = null, double? fuzzyTolerance = null, ReferencePulseRules? reference = null, int unitsPerSample = 1, bool halfSample = false, Action<SharpPulseDiagnostic>? diagnostic = null)
        {
            this.diagnostic = diagnostic; this.reference = reference; this.unitsPerSample = unitsPerSample; this.halfSample = halfSample;
            this.fixedShortUnits = reference?.ShortSamples ?? fixedShortUnits;
            this.fuzzyTolerance = fuzzyTolerance;
        }

        private enum DecodeState
        {
            SearchLeader,
            MarkLong,
            MarkShort,
            MarkFinal,
            Data,
            DuplicateGap
        }

        private enum DecoderMode
        {
            Stopped,
            Header,
            Data
        }

        private DecoderMode mode;
        private DecodeState state;
        private long shortX8;
        private int leaderPulses;
        private int markPulses;
        private int finalPulses;
        private int bitCount;
        private byte byteValue;
        private int byteIndex;
        private int expectedBytes;
        private ushort checksum;
        private ushort recordedChecksum;
        private int copyIndex;
        private long physicalHighUnitsTotal;
        private int physicalHighIntervals;
        private long markLongPhysicalLowUnitsTotal;
        private int markLongPhysicalLowIntervals;
        private long markLongPhysicalHighUnitsTotal;
        private int markLongPhysicalHighIntervals;
        private bool expectMarkLongPhysicalHigh;
        private double leaderMean;
        private double leaderM2;
        private int leaderObservationCount;
        private int classifiedPulses;
        private int unclassifiedPulses;
        private readonly byte[] headerBuffer = new byte[HeaderBytes];
        private byte[]? validatedHeader;
        private long validatedShortX8;
        private long validatedLeaderPhysicalLowMeanX8;
        private long validatedPhysicalHighX8;
        private long validatedLongPhysicalLowX8;
        private long validatedLongPhysicalHighX8;
        private long completedLeaderPhysicalLowMeanX8;
        private long completedShortPhysicalHighX8;
        private long completedLongPhysicalLowX8;
        private long completedLongPhysicalHighX8;
        private SharpMzDecoderEvent? pendingEvent;
        private long intervalEndUnits;
        private long leaderStartUnits, syncStartUnits = -1, dataStartUnits = -1, checksumStartUnits = -1;

        internal byte[]? ValidatedHeader => validatedHeader;
        internal long HeaderShortPhysicalLowX8 => ToSamples(validatedShortX8);
        internal long HeaderLeaderPhysicalLowMeanX8 => ToSamples(validatedLeaderPhysicalLowMeanX8);
        internal long HeaderShortPhysicalHighX8 => ToSamples(validatedPhysicalHighX8);
        internal long HeaderLongPhysicalLowX8 => ToSamples(validatedLongPhysicalLowX8);
        internal long HeaderLongPhysicalHighX8 => ToSamples(validatedLongPhysicalHighX8);
        internal long CompletedLeaderPhysicalLowMeanX8 => ToSamples(completedLeaderPhysicalLowMeanX8);
        internal long CompletedShortPhysicalHighX8 => ToSamples(completedShortPhysicalHighX8);
        internal long CompletedLongPhysicalLowX8 => ToSamples(completedLongPhysicalLowX8);
        internal long CompletedLongPhysicalHighX8 => ToSamples(completedLongPhysicalHighX8);
        internal double LeaderAverage => leaderMean / unitsPerSample;
        internal double LeaderStdDev => leaderObservationCount > 1
            ? Math.Sqrt(leaderM2 / (leaderObservationCount - 1)) / unitsPerSample
            : 0;
        internal double PulseConfidence => classifiedPulses + unclassifiedPulses == 0
            ? 0
            : (double)classifiedPulses / (classifiedPulses + unclassifiedPulses);

        internal void BeginHeader()
        {
            unknownLength = false;
            mode = DecoderMode.Header;
            validatedHeader = null;
            validatedShortX8 = 0;
            validatedLeaderPhysicalLowMeanX8 = 0;
            validatedPhysicalHighX8 = 0;
            validatedLongPhysicalLowX8 = 0;
            validatedLongPhysicalHighX8 = 0;
            ClearCompletedTiming();
            pendingEvent = null;
            expectedBytes = HeaderBytes;
            ResetDecoder(0);
        }

        internal void StartData(int byteCount)
        {
            if (validatedHeader is null || byteCount is < 0 or > ushort.MaxValue)
            {
                Stop();
                return;
            }

            mode = DecoderMode.Data;
            pendingEvent = null;
            expectedBytes = byteCount;
            ResetDecoder(0);
        }

        internal void StartRecoveryData(int byteCount)
        {
            if (validatedHeader is null || byteCount is < 0 or > ushort.MaxValue)
            {
                Stop();
                return;
            }

            mode = DecoderMode.Data;
            pendingEvent = null;
            BeginDuplicateGap(byteCount);
        }

        internal void BeginRawBlock(int byteCount)
        {
            unknownLength = false;
            if (byteCount is < 0 or > ushort.MaxValue)
            {
                Stop();
                return;
            }

            mode = DecoderMode.Data;
            ClearCompletedTiming();
            pendingEvent = null;
            expectedBytes = byteCount;
            ResetDecoder(0);
        }

        internal void BeginUnknownRawBlock()
        {
            BeginRawBlock(ushort.MaxValue);
            unknownLength = true;
        }
        internal void FinishSelectedInterval()
        {
            if (unknownLength) CompleteUnknownBlock("SELECTED_END");
            else if (state == DecodeState.Data) Diagnose("BLOCK_TRUNCATED");
        }
        private bool CompleteUnknownBlock(string boundary)
        {
            if (!unknownLength || mode != DecoderMode.Data || state != DecodeState.Data || byteIndex < 3) return false;
            ushort candidateRecorded = (ushort)((penultimateByte << 8) | lastByte);
            ushort candidateCalculated = unchecked((ushort)(checksum - BitOperations.PopCount(penultimateByte) - BitOperations.PopCount(lastByte)));
            // A malformed trailing byte may follow a complete checksum. Preserve
            // that bounded possibility, with unverified length, only on a match.
            if (bitCount != 0 && candidateRecorded != candidateCalculated) return false;
            recordedChecksum = candidateRecorded; checksum = candidateCalculated;
            expectedBytes = byteIndex - 2;
            CaptureCompletedTiming();
            long savedEnd = intervalEndUnits; intervalEndUnits = unknownDataEnd;
            Diagnose(boundary + (bitCount != 0 ? "_AFTER_PARTIAL_BYTE" : ""));
            PublishEvent(recordedChecksum == checksum ? SharpMzDecoderEventType.BlockValid : SharpMzDecoderEventType.BlockInvalid, 0, expectedBytes);
            intervalEndUnits = savedEnd; mode = DecoderMode.Stopped;
            return true;
        }

        internal void BreakSignal()
        {
            if (CompleteUnknownBlock("FRAMING_BOUNDARY")) return;
            if (state == DecodeState.Data) Diagnose("BLOCK_TRUNCATED");
            if (mode == DecoderMode.Header)
            {
                expectedBytes = HeaderBytes;
                ResetDecoder(0);
            }
            else if (mode == DecoderMode.Data)
            {
                ResetDecoder(0);
            }
        }

        internal void Stop()
        {
            mode = DecoderMode.Stopped;
            pendingEvent = null;
        }

        internal bool FeedMeasuredInterval(double samples, bool physicalHigh) => FeedInterval((long)Math.Round(samples * unitsPerSample), physicalHigh);

        internal bool FeedInterval(long durationUnits, bool physicalHigh)
        {
            intervalEndUnits += Math.Max(0, durationUnits);
            recentPulses[recentPulseIndex++ % recentPulses.Length] = durationUnits / (double)unitsPerSample;
            if (mode == DecoderMode.Stopped)
            {
                return false;
            }

            if (durationUnits <= 0 || durationUnits > MaxHalfUnits * unitsPerSample)
            {
                BreakSignal();
                return false;
            }

            if (physicalHigh)
            {
                if (reference != null && previousReferenceClass >= 0 && reference.Classify(durationUnits / (double)unitsPerSample, false) != previousReferenceClass)
                { previousReferenceClass = -1; BreakSignal(); return false; }
                TrackLeaderPhysicalHigh(durationUnits);
                TrackMarkLongPhysicalHigh(durationUnits);
                return false;
            }

            previousReferenceClass = reference?.Classify(durationUnits / (double)unitsPerSample, true) ?? -1;
            FeedPulse(durationUnits);
            return pendingEvent.HasValue;
        }

        internal bool TryTakeEvent(out SharpMzDecoderEvent decoderEvent)
        {
            if (pendingEvent is not SharpMzDecoderEvent value)
            {
                decoderEvent = default;
                return false;
            }

            decoderEvent = value;
            pendingEvent = null;
            return true;
        }

        private void ResetDecoder(long seedUnits, string reason = "RESET")
        {
            if (state != DecodeState.SearchLeader && mode != DecoderMode.Stopped) Diagnose(reason, seedUnits);
            penultimateByte = lastByte = 0; beforeLastByteEnd = lastByteEnd = unknownDataEnd = intervalEndUnits;
            leaderStartUnits = intervalEndUnits - Math.Max(0, seedUnits);
            syncStartUnits = dataStartUnits = checksumStartUnits = -1;
            state = DecodeState.SearchLeader;
            shortX8 = seedUnits <= MaxHalfUnits * unitsPerSample ? seedUnits * 8 : 0;
            if (seedUnits > 0 && fixedShortUnits.HasValue) shortX8 = Math.Max(1, (long)Math.Round(fixedShortUnits.Value * unitsPerSample * 8));
            leaderPulses = shortX8 != 0 ? 1 : 0;
            markPulses = 0;
            finalPulses = 0;
            bitCount = 0;
            byteValue = 0;
            byteIndex = 0;
            checksum = 0;
            recordedChecksum = 0;
            copyIndex = 0;
            physicalHighUnitsTotal = 0;
            physicalHighIntervals = 0;
            markLongPhysicalLowUnitsTotal = 0;
            markLongPhysicalLowIntervals = 0;
            markLongPhysicalHighUnitsTotal = 0;
            markLongPhysicalHighIntervals = 0;
            expectMarkLongPhysicalHigh = false;
            leaderMean = 0;
            leaderM2 = 0;
            leaderObservationCount = 0;
            classifiedPulses = 0;
            unclassifiedPulses = 0;
            if (seedUnits > 0)
            {
                ObserveLeader(seedUnits);
            }
        }

        private bool AcceptLeaderPulse(long durationUnits)
        {
            if (reference != null)
            {
                if (reference.Classify(durationUnits / (double)unitsPerSample, true) != 0) return false;
                ObserveLeader(durationUnits); return true;
            }
            if (shortX8 == 0)
            {
                return false;
            }

            long scaled = durationUnits * 8;
            long difference = Math.Abs(scaled - shortX8);
            long tolerance = Math.Max((long)(shortX8 * (fuzzyTolerance ?? .25)), 4);
            if (difference > tolerance + (halfSample ? unitsPerSample * 4.0 : 0))
            {
                return false;
            }

            if (!fixedShortUnits.HasValue) shortX8 = scaled >= shortX8
                ? shortX8 + ((difference + 4) >> 3)
                : shortX8 - ((difference + 3) >> 3);
            ObserveLeader(durationUnits);
            return true;
        }

        private void LockLeaderWindow()
        {
            if (halfSample || unitsPerSample != 1 || fixedShortUnits.HasValue || fuzzyTolerance.HasValue) return;
            // The reference locks only its real-pulse 8-bit hot path.
            if (finalPulses != 0 || shortX8 is <= 0 or > 204)
            {
                return;
            }

            long tolerance = Math.Max(shortX8 / 4, 4);
            long lowScaled = Math.Max(0, shortX8 - tolerance);
            long highScaled = shortX8 + tolerance;
            markPulses = (int)((lowScaled + 7) >> 3);
            finalPulses = (int)(highScaled >> 3);
        }

        private int ClassifyPulse(long durationUnits)
        {
            if (reference != null) return reference.Classify(durationUnits / (double)unitsPerSample, true);
            int original = ClassifyPulseCore(durationUnits);
            if (!halfSample || original >= 0) return original;
            double half = unitsPerSample * .5;
            int minus = durationUnits > half ? ClassifyPulseCore(durationUnits - half) : -1;
            int plus = ClassifyPulseCore(durationUnits + half);
            // Preserve accepted measurements; rescue rejected lengths only when
            // the two neighbouring hypotheses do not disagree on SHORT/LONG.
            return minus >= 0 && plus >= 0 && minus != plus ? -1 : Math.Max(minus, plus);
        }

        private int ClassifyPulseCore(double durationUnits)
        {
            if (shortX8 == 0)
            {
                return -1;
            }

            double scaled = durationUnits * 8;
            if (fuzzyTolerance.HasValue)
            {
                double ratio = scaled / (double)shortX8;
                double shortError = Math.Abs(ratio - 1), longError = Math.Abs(ratio / 2 - 1);
                if (Math.Min(shortError, longError) > fuzzyTolerance.Value) return -1;
                return shortError <= longError ? 0 : 1;
            }
            if ((2 * scaled) < shortX8 || scaled > (3 * shortX8))
            {
                return -1;
            }

            return (20 * scaled) < (29 * shortX8) ? 0 : 1;
        }

        private void FeedPulse(long durationUnits)
        {
            if (mode == DecoderMode.Stopped)
            {
                return;
            }

            if (state == DecodeState.DuplicateGap)
            {
                int pulseClass = ClassifyPulse(durationUnits);
                RecordClassification(pulseClass);
                if (pulseClass == 0)
                {
                    if (leaderPulses < 256)
                    {
                        leaderPulses++;
                    }
                    if (leaderPulses == 256)
                    {
                        dataStartUnits = intervalEndUnits;
                        Diagnose("MARK_FOUND", durationUnits);
                        state = DecodeState.Data;
                        leaderPulses = 0;
                    }
                }
                else if (leaderPulses < 128)
                {
                    leaderPulses = 0;
                }
                else
                {
                    ResetDecoder(durationUnits);
                }
                return;
            }

            if (state == DecodeState.SearchLeader)
            {
                if (shortX8 == 0)
                {
                    ResetDecoder(durationUnits);
                    return;
                }

                if (finalPulses != 0 &&
                    durationUnits >= markPulses &&
                    durationUnits <= finalPulses)
                {
                    if (leaderPulses < ushort.MaxValue)
                    {
                        leaderPulses++;
                    }
                    return;
                }

                if (finalPulses == 0 && AcceptLeaderPulse(durationUnits))
                {
                    if (leaderPulses < ushort.MaxValue)
                    {
                        leaderPulses++;
                    }
                    if (leaderPulses == LeaderLockPulses)
                    {
                        LockLeaderWindow();
                    }
                    return;
                }

                int pulseClass = ClassifyPulse(durationUnits);
                RecordClassification(pulseClass);
                if (leaderPulses >= MinLeaderPulses && pulseClass == 1)
                {
                    ObserveMarkLongPhysicalLow(durationUnits);
                    state = DecodeState.MarkLong;
                    Diagnose("LEADER_FOUND", durationUnits);
                    syncStartUnits = intervalEndUnits - durationUnits;
                    markPulses = 1;
                    return;
                }

                ResetDecoder(durationUnits);
                return;
            }

            int classified = ClassifyPulse(durationUnits);
            RecordClassification(classified);
            if (classified < 0)
            {
                if (CompleteUnknownBlock("PULSE_BOUNDARY")) return;
                ResetDecoder(durationUnits, "PULSE_CLASSIFICATION_RESET");
                return;
            }

            if (state == DecodeState.MarkLong)
            {
                if (classified == 1)
                {
                    ObserveMarkLongPhysicalLow(durationUnits);
                    if (markPulses < byte.MaxValue)
                    {
                        markPulses++;
                    }
                    return;
                }
                if (markPulses is >= MinMarkPulses and <= MaxMarkPulses)
                {
                    state = DecodeState.MarkShort;
                    markPulses = 1;
                    return;
                }
                ResetDecoder(durationUnits);
                return;
            }

            if (state == DecodeState.MarkShort)
            {
                if (classified == 0)
                {
                    if (markPulses < byte.MaxValue)
                    {
                        markPulses++;
                    }
                    return;
                }
                if (markPulses is >= MinMarkPulses and <= MaxMarkPulses)
                {
                    state = DecodeState.MarkFinal;
                    finalPulses = 1;
                    return;
                }
                ResetDecoder(durationUnits);
                return;
            }

            if (state == DecodeState.MarkFinal)
            {
                if (classified != 1)
                {
                    ResetDecoder(durationUnits);
                    return;
                }
                finalPulses++;
                if (finalPulses == FinalMarkPulses)
                {
                    state = DecodeState.Data;
                    dataStartUnits = intervalEndUnits;
                    Diagnose("MARK_FOUND", durationUnits);
                    if (expectedBytes == 0) checksumStartUnits = dataStartUnits;
                }
                return;
            }

            AcceptDataPulse((byte)classified);
        }

        private void AcceptDataPulse(byte pulseClass)
        {
            if (bitCount < 8)
            {
                byteValue = (byte)((byteValue << 1) | pulseClass);
                bitCount++;
                return;
            }

            if (pulseClass != 1)
            {
                if (CompleteUnknownBlock("BAD_BYTE_STOP_BOUNDARY")) return;
                ResetDecoder(0, "BAD_BYTE_STOP");
                return;
            }

            AcceptByte(byteValue);
            byteValue = 0;
            bitCount = 0;
        }

        private void AcceptByte(byte value)
        {
            int index = byteIndex;
            if (unknownLength)
            {
                if (index >= ushort.MaxValue + 2) { Diagnose("LENGTH_LIMIT"); mode = DecoderMode.Stopped; return; }
                checksum = unchecked((ushort)(checksum + BitOperations.PopCount(value)));
                if (index >= 2) checksumStartUnits = beforeLastByteEnd;
                beforeLastByteEnd = lastByteEnd; lastByteEnd = unknownDataEnd = intervalEndUnits;
                penultimateByte = lastByte; lastByte = value;
                PublishEvent(SharpMzDecoderEventType.DataByte, value, index); byteIndex++;
                return;
            }
            if (index < expectedBytes)
            {
                checksum = unchecked((ushort)(checksum + BitOperations.PopCount(value)));
                if (mode == DecoderMode.Header)
                {
                    headerBuffer[index] = value;
                }
                else
                {
                    PublishEvent(SharpMzDecoderEventType.DataByte, value, index);
                }
            }
            else
            {
                recordedChecksum = (ushort)((recordedChecksum << 8) | value);
            }

            byteIndex++;
            if (byteIndex == expectedBytes) checksumStartUnits = intervalEndUnits;
            if (byteIndex != expectedBytes + 2)
            {
                return;
            }

            bool valid = recordedChecksum == checksum;
            Diagnose(valid ? "VERIFIED_CHECKSUM" : "CHECKSUM_MISMATCH");
            CaptureCompletedTiming();
            if (mode == DecoderMode.Header)
            {
                if (valid)
                {
                    validatedHeader = (byte[])headerBuffer.Clone();
                    validatedShortX8 = shortX8;
                    validatedLeaderPhysicalLowMeanX8 = completedLeaderPhysicalLowMeanX8;
                    validatedPhysicalHighX8 = completedShortPhysicalHighX8;
                    validatedLongPhysicalLowX8 = completedLongPhysicalLowX8;
                    validatedLongPhysicalHighX8 = completedLongPhysicalHighX8;
                    mode = DecoderMode.Stopped;
                    PublishEvent(SharpMzDecoderEventType.HeaderValid, 0, 0);
                }
                else
                {
                    PublishEvent(SharpMzDecoderEventType.HeaderInvalid, 0, 0);
                    BeginDuplicateGap(HeaderBytes);
                }
                return;
            }

            PublishEvent(
                valid ? SharpMzDecoderEventType.BlockValid : SharpMzDecoderEventType.BlockInvalid,
                0,
                expectedBytes);
            mode = DecoderMode.Stopped;
        }

        private void BeginDuplicateGap(int byteCount)
        {
            leaderStartUnits = intervalEndUnits;
            syncStartUnits = dataStartUnits = checksumStartUnits = -1;
            state = DecodeState.DuplicateGap;
            leaderPulses = 0;
            markPulses = 0;
            finalPulses = 0;
            bitCount = 0;
            byteValue = 0;
            byteIndex = 0;
            expectedBytes = byteCount;
            checksum = 0;
            recordedChecksum = 0;
            copyIndex = 1;
        }

        private long ToSamples(long units) => (long)Math.Round(units / (double)unitsPerSample);

        private void PublishEvent(SharpMzDecoderEventType type, byte value, int index)
        {
            pendingEvent ??= new SharpMzDecoderEvent(
                type,
                value,
                index,
                checksum,
                recordedChecksum,
                leaderPulses,
                copyIndex)
            {
                SignalTrace = syncStartUnits >= 0 && dataStartUnits >= 0 && checksumStartUnits >= 0
                    ? new(ToSamples(leaderStartUnits), ToSamples(syncStartUnits), ToSamples(dataStartUnits), ToSamples(checksumStartUnits), ToSamples(intervalEndUnits)) : null
            };
        }

        private void ClearCompletedTiming()
        {
            completedLeaderPhysicalLowMeanX8 = 0;
            completedShortPhysicalHighX8 = 0;
            completedLongPhysicalLowX8 = 0;
            completedLongPhysicalHighX8 = 0;
        }

        private void CaptureCompletedTiming()
        {
            completedLeaderPhysicalLowMeanX8 = leaderObservationCount == 0
                ? 0
                : checked((long)Math.Round(leaderMean * 8.0));
            completedShortPhysicalHighX8 = physicalHighIntervals == 0
                ? 0
                : ((physicalHighUnitsTotal * 8) + (physicalHighIntervals / 2)) /
                    physicalHighIntervals;
            completedLongPhysicalLowX8 = markLongPhysicalLowIntervals == 0
                ? 0
                : ((markLongPhysicalLowUnitsTotal * 8) + (markLongPhysicalLowIntervals / 2)) /
                    markLongPhysicalLowIntervals;
            completedLongPhysicalHighX8 = markLongPhysicalHighIntervals == 0
                ? 0
                : ((markLongPhysicalHighUnitsTotal * 8) + (markLongPhysicalHighIntervals / 2)) /
                    markLongPhysicalHighIntervals;
        }

        private void ObserveMarkLongPhysicalLow(long durationUnits)
        {
            if (markLongPhysicalLowIntervals == int.MaxValue)
            {
                return;
            }
            markLongPhysicalLowUnitsTotal = checked(markLongPhysicalLowUnitsTotal + durationUnits);
            markLongPhysicalLowIntervals++;
            expectMarkLongPhysicalHigh = true;
        }

        private void TrackMarkLongPhysicalHigh(long durationUnits)
        {
            if (!expectMarkLongPhysicalHigh)
            {
                return;
            }
            expectMarkLongPhysicalHigh = false;
            if (markLongPhysicalHighIntervals == int.MaxValue)
            {
                return;
            }
            markLongPhysicalHighUnitsTotal = checked(markLongPhysicalHighUnitsTotal + durationUnits);
            markLongPhysicalHighIntervals++;
        }

        private void TrackLeaderPhysicalHigh(long durationUnits)
        {
            if (state != DecodeState.SearchLeader || leaderPulses == 0)
            {
                return;
            }

            if (physicalHighIntervals == int.MaxValue)
            {
                return;
            }
            physicalHighUnitsTotal = checked(physicalHighUnitsTotal + durationUnits);
            physicalHighIntervals++;
        }

        private void ObserveLeader(long durationUnits)
        {
            leaderObservationCount++;
            double delta = durationUnits - leaderMean;
            leaderMean += delta / leaderObservationCount;
            leaderM2 += delta * (durationUnits - leaderMean);
        }

        private void RecordClassification(int pulseClass)
        {
            if (pulseClass < 0)
            {
                unclassifiedPulses++;
            }
            else
            {
                classifiedPulses++;
            }
        }

    }
}
