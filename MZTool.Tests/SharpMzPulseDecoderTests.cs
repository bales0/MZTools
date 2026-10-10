namespace MZTools.Tests;

public class SharpMzPulseDecoderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownRawBlock_UsesOneStreamingDecoderAndChecksFramedEnd(bool corrupt)
    {
        var decoder = new SharpMzPulseDecoder(); var events = new List<SharpMzDecoderEvent>();
        byte[] body = [1, 2, 3, 0x80, 0xFF]; var timing = new Timing("unknown", 10, 12, 20, 22);
        decoder.BeginUnknownRawBlock(); FeedFramedBlock(decoder, timing, body, events, corruptChecksum: corrupt);
        decoder.FinishSelectedInterval(); while (decoder.TryTakeEvent(out var item)) events.Add(item);
        var completed = Assert.Single(events, e => e.Type is SharpMzDecoderEventType.BlockValid or SharpMzDecoderEventType.BlockInvalid);
        Assert.Equal(body.Length, completed.ByteIndex);
        Assert.Equal(corrupt ? SharpMzDecoderEventType.BlockInvalid : SharpMzDecoderEventType.BlockValid, completed.Type);
        Assert.Equal(body, events.Where(e => e.Type == SharpMzDecoderEventType.DataByte).Take(body.Length).Select(e => e.Value));
        Assert.True(completed.SignalTrace!.IsOrdered);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CommonHalfSample_RecoversRejectedLengthsWithoutReferencesAndPreservesTiming(bool fixedTiming, bool fuzzy)
    {
        // Eight units per sample: LONG is 30.5 samples for the normal 3×SHORT
        // limit, or 27 samples for fuzzy's 33% tolerance around 20 samples.
        var timing = new Timing("fractional boundary", 80, 80, fuzzy ? 216 : 244, fuzzy ? 216 : 244);
        var rejected = new SharpMzPulseDecoder(fixedTiming ? 10 : null, fuzzy ? .33 : null, unitsPerSample: 8);
        var corrected = new SharpMzPulseDecoder(fixedTiming ? 10 : null, fuzzy ? .33 : null, unitsPerSample: 8, halfSample: true);
        var baseline = new SharpMzPulseDecoder(fixedTiming ? 10 : null, .6, unitsPerSample: 8);
        var rejectedEvents = new List<SharpMzDecoderEvent>(); var correctedEvents = new List<SharpMzDecoderEvent>(); var baselineEvents = new List<SharpMzDecoderEvent>();
        rejected.BeginHeader(); corrected.BeginHeader(); baseline.BeginHeader();
        byte[] header = CreateHeader();
        FeedFramedBlock(rejected, timing, header, rejectedEvents); FeedFramedBlock(corrected, timing, header, correctedEvents); FeedFramedBlock(baseline, timing, header, baselineEvents);
        Assert.DoesNotContain(rejectedEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid);
        var valid = Assert.Single(correctedEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.Equal(header, corrected.ValidatedHeader);
        Assert.Equal(Assert.Single(baselineEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid).SignalTrace, valid.SignalTrace);
        Assert.Equal(baseline.HeaderLeaderPhysicalLowMeanX8, corrected.HeaderLeaderPhysicalLowMeanX8);
    }
    [Fact]
    public void ReferenceHalfSample_IsPerPulseAndPreservesSignalTrace()
    {
        var reference = AudioPulseReference.User("Half sample", 10.5, 10.5, 21.5, 21.5);
        var timing = new Timing("quantized", 10, 10, 21, 21);
        var strict = new SharpMzPulseDecoder(reference: new(reference, 1000000, .01, false));
        var corrected = new SharpMzPulseDecoder(reference: new(reference, 1000000, .01, true));
        var wider = new SharpMzPulseDecoder(reference: new(reference, 1000000, .06, false));
        var strictEvents = new List<SharpMzDecoderEvent>(); var correctedEvents = new List<SharpMzDecoderEvent>(); var widerEvents = new List<SharpMzDecoderEvent>();
        strict.BeginHeader(); corrected.BeginHeader(); wider.BeginHeader();
        var header = CreateHeader();
        FeedFramedBlock(strict, timing, header, strictEvents);
        FeedFramedBlock(corrected, timing, header, correctedEvents);
        FeedFramedBlock(wider, timing, header, widerEvents);
        Assert.DoesNotContain(strictEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid);
        var recovered = Assert.Single(correctedEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid);
        var baseline = Assert.Single(widerEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.Equal(header, corrected.ValidatedHeader);
        Assert.NotNull(recovered.SignalTrace); Assert.Equal(baseline.SignalTrace, recovered.SignalTrace);
        Assert.Equal(wider.HeaderLeaderPhysicalLowMeanX8, corrected.HeaderLeaderPhysicalLowMeanX8);
        Assert.Equal(-1, new ReferencePulseRules(reference, 1000000, .01, true).Classify(9.5, true)); // no ±1 search
    }

    [Fact]
    public void ReferenceRules_CheckAllFourGroupsAndContextValues()
    {
        var reference = new AudioPulseReference("asymmetric", "test", "test", [new("SHORT High", 10, "leader"), new("SHORT Low", 14, "leader"), new("SHORT Low", 15, "data"), new("LONG High", 20, "mark"), new("LONG Low", 28, "data")]);
        var rules = new ReferencePulseRules(reference, 1000000, .01, false);
        Assert.Equal(0, rules.Classify(10, true)); Assert.Equal(-1, rules.Classify(10, false));
        Assert.Equal(0, rules.Classify(14, false)); Assert.Equal(0, rules.Classify(15, false));
        Assert.Equal(1, rules.Classify(20, true)); Assert.Equal(1, rules.Classify(28, false));
        Assert.Equal(-1, rules.Classify(28, true));
    }
    [Fact]
    public void ManualFuzzyTest_RecoversShortenedLongPulsesWithoutChangingDefaultDecoder()
    {
        var timing = new Timing("shortened LONG", 10, 10, 14, 14);
        byte[] header = CreateHeader();
        var standard = new SharpMzPulseDecoder(); var fuzzy = new SharpMzPulseDecoder(fuzzyTolerance: .35);
        var standardEvents = new List<SharpMzDecoderEvent>(); var fuzzyEvents = new List<SharpMzDecoderEvent>();
        standard.BeginHeader(); fuzzy.BeginHeader();
        FeedFramedBlock(standard, timing, header, standardEvents); FeedFramedBlock(fuzzy, timing, header, fuzzyEvents);
        Assert.DoesNotContain(standardEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.Contains(fuzzyEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.Equal(header, fuzzy.ValidatedHeader);
    }

    [Fact]
    public void ManualFixedTiming_UsesConfiguredReferenceAndPreservesMeasuredTracePositions()
    {
        var timing = new Timing("fixed SHORT", 10, 12, 20, 22);
        byte[] header = CreateHeader();
        var correct = new SharpMzPulseDecoder(fixedShortUnits: 10); var wrong = new SharpMzPulseDecoder(fixedShortUnits: 20);
        var correctEvents = new List<SharpMzDecoderEvent>(); var wrongEvents = new List<SharpMzDecoderEvent>();
        correct.BeginHeader(); wrong.BeginHeader();
        FeedFramedBlock(correct, timing, header, correctEvents); FeedFramedBlock(wrong, timing, header, wrongEvents);
        var valid = Assert.Single(correctEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.DoesNotContain(wrongEvents, e => e.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.Equal(header, correct.ValidatedHeader);
        Assert.NotNull(valid.SignalTrace); Assert.True(valid.SignalTrace.IsOrdered);
    }

    public static TheoryData<string, int, int, int, int> ReferenceProfiles => new()
    {
        { "NORMAL MZ800 1:1", 15, 16, 29, 30 },
        { "NORMAL MZ700 1:1", 15, 17, 29, 31 },
        { "NORMAL 1:2", 7, 9, 15, 16 },
        { "NORMAL 1:3", 5, 8, 11, 14 },
        { "NORMAL 1:4", 5, 7, 10, 11 },
        { "IC 1:4", 5, 7, 10, 11 },
        { "IC 1:3", 5, 8, 11, 14 },
        { "IC 1:2", 7, 9, 15, 16 },
        { "TC 1:3", 7, 7, 13, 13 },
        { "TC 1:2", 9, 9, 18, 18 }
    };

    [Theory]
    [MemberData(nameof(ReferenceProfiles))]
    public void Decoder_DecodesEveryReferenceProfile(
        string name,
        int shortPhysicalLow,
        int shortPhysicalHigh,
        int longPhysicalLow,
        int longPhysicalHigh)
    {
        var timing = new Timing(
            name,
            shortPhysicalLow,
            shortPhysicalHigh,
            longPhysicalLow,
            longPhysicalHigh);
        byte[] header = CreateHeader();
        var decoder = new SharpMzPulseDecoder();
        var events = new List<SharpMzDecoderEvent>();

        decoder.BeginHeader();
        FeedFramedBlock(decoder, timing, header, events);

        SharpMzDecoderEvent valid = Assert.Single(
            events,
            value => value.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.Equal(header, decoder.ValidatedHeader);
        Assert.Equal(shortPhysicalLow * 8, decoder.HeaderShortPhysicalLowX8);
        Assert.Equal(0, valid.CopyIndex);
    }

    [Fact]
    public void Decoder_ClassifiesOnlyPhysicalLow_WhenOppositeHalfHasOppositeClass()
    {
        // SHORT has a LONG physical-HIGH half and LONG has a SHORT one. A
        // full-period classifier sees identical 26-unit periods.
        var timing = new Timing("LOW-only", 9, 17, 18, 8);
        var decoder = new SharpMzPulseDecoder();
        var events = new List<SharpMzDecoderEvent>();

        decoder.BeginHeader();
        FeedFramedBlock(decoder, timing, CreateHeader(), events);

        Assert.Contains(events, value => value.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.Equal(72, decoder.HeaderShortPhysicalLowX8);
    }

    [Fact]
    public void Decoder_DoesNotSilentlyTryInvertedPolarity()
    {
        var timing = new Timing("asymmetric", 5, 12, 10, 12);
        byte[] header = CreateHeader();

        Assert.True(DecodesHeader(timing, header, invert: false));
        Assert.False(DecodesHeader(timing, header, invert: true));
    }

    [Theory]
    [InlineData(15, false)]
    [InlineData(16, true)]
    public void Decoder_RequiresMinimumLeaderLength(int leaderPulses, bool expectedValid)
    {
        var timing = new Timing("leader boundary", 10, 12, 20, 22);
        var decoder = new SharpMzPulseDecoder();
        var events = new List<SharpMzDecoderEvent>();
        decoder.BeginHeader();

        FeedLeaderAndMark(decoder, timing, events, leaderPulses, 12, 12);
        FeedBytesAndChecksum(decoder, timing, CreateHeader(), events);

        Assert.Equal(
            expectedValid,
            events.Any(value => value.Type == SharpMzDecoderEventType.HeaderValid));
    }

    [Theory]
    [InlineData(11, 12, false)]
    [InlineData(12, 12, true)]
    [InlineData(48, 48, true)]
    [InlineData(49, 12, false)]
    [InlineData(12, 11, false)]
    [InlineData(12, 49, false)]
    public void Decoder_EnforcesMarkBoundaries(
        int longMarkPulses,
        int shortMarkPulses,
        bool expectedValid)
    {
        var timing = new Timing("mark boundary", 10, 12, 20, 22);
        var decoder = new SharpMzPulseDecoder();
        var events = new List<SharpMzDecoderEvent>();
        decoder.BeginHeader();

        FeedLeaderAndMark(decoder, timing, events, 32, longMarkPulses, shortMarkPulses);
        FeedBytesAndChecksum(decoder, timing, Enumerable.Repeat((byte)0xFF, 128).ToArray(), events);

        Assert.Equal(
            expectedValid,
            events.Any(value => value.Type == SharpMzDecoderEventType.HeaderValid));
    }

    [Fact]
    public void Decoder_LocksLeaderAfter256PulsesAndAcceptsFrozenWindowEdges()
    {
        var timing = new Timing("locked leader", 10, 12, 20, 22);
        var decoder = new SharpMzPulseDecoder();
        var events = new List<SharpMzDecoderEvent>();
        decoder.BeginHeader();

        for (int index = 0; index < 256; index++)
        {
            FeedPulse(decoder, timing, isLong: false, events);
        }
        FeedRawPulse(decoder, physicalLow: 8, physicalHigh: 12, events);
        FeedRawPulse(decoder, physicalLow: 12, physicalHigh: 12, events);
        FeedMarkAndData(decoder, timing, CreateHeader(), events, 12, 12);

        Assert.Contains(events, value => value.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.Equal(80, decoder.HeaderShortPhysicalLowX8);
    }

    [Fact]
    public void Decoder_RecoversAlignmentAfterShortByteSync()
    {
        var timing = new Timing("byte sync", 10, 12, 20, 22);
        var decoder = new SharpMzPulseDecoder();
        var events = new List<SharpMzDecoderEvent>();
        decoder.BeginHeader();

        FeedLeaderAndMark(decoder, timing, events);
        for (int bit = 0; bit < 8; bit++)
        {
            FeedPulse(decoder, timing, isLong: false, events);
        }
        FeedPulse(decoder, timing, isLong: false, events); // invalid sync
        FeedFramedBlock(decoder, timing, CreateHeader(), events);

        Assert.Single(events, value => value.Type == SharpMzDecoderEventType.HeaderValid);
    }

    [Fact]
    public void Decoder_RecoversDuplicateHeaderAfterInvalidChecksum()
    {
        var timing = new Timing("duplicate header", 15, 16, 29, 30);
        byte[] header = CreateHeader();
        var decoder = new SharpMzPulseDecoder();
        var events = new List<SharpMzDecoderEvent>();
        decoder.BeginHeader();

        FeedFramedBlock(decoder, timing, header, events, corruptChecksum: true);
        Assert.DoesNotContain(events, value => value.Type == SharpMzDecoderEventType.HeaderValid);
        for (int index = 0; index < 256; index++)
        {
            FeedPulse(decoder, timing, isLong: false, events);
        }
        FeedBytesAndChecksum(decoder, timing, header, events);

        SharpMzDecoderEvent valid = Assert.Single(
            events,
            value => value.Type == SharpMzDecoderEventType.HeaderValid);
        Assert.Equal(1, valid.CopyIndex);
    }

    [Fact]
    public void Decoder_ReportsDataChecksumAndRecoversDuplicateCopy()
    {
        var timing = new Timing("data recovery", 15, 16, 29, 30);
        byte[] payload = [0xA5, 0x00, 0x7E];
        var decoder = new SharpMzPulseDecoder();
        var events = new List<SharpMzDecoderEvent>();
        decoder.BeginHeader();
        FeedFramedBlock(decoder, timing, CreateHeader(), events);

        decoder.StartData(payload.Length);
        FeedFramedBlock(decoder, timing, payload, events, corruptChecksum: true);
        Assert.Contains(events, value => value.Type == SharpMzDecoderEventType.BlockInvalid);

        decoder.StartRecoveryData(payload.Length);
        for (int index = 0; index < 256; index++)
        {
            FeedPulse(decoder, timing, isLong: false, events);
        }
        FeedBytesAndChecksum(decoder, timing, payload, events);

        SharpMzDecoderEvent valid = Assert.Single(
            events,
            value => value.Type == SharpMzDecoderEventType.BlockValid);
        Assert.Equal(1, valid.CopyIndex);
    }

    [Fact]
    public void Importer_ReadsRealTurboCopyL16Capture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "LEP", "turbocopy.l16");

        IReadOnlyList<TapeRecord> records = SharpTapeImporter.ReadFile(path);

        TapeRecord record = Assert.Single(records);
        Assert.Equal((ushort)7083, record.Header.MzfSize);
        Assert.Equal((ushort)0x1200, record.Header.MzfStart);
        Assert.Equal((ushort)0x2D98, record.Header.MzfExec);
        Assert.Equal("Turbo Copy V1.22", SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname));
        Assert.Equal((ushort)0x6393, ComputeChecksum(record.Body.MzfBody));

        SharpMzDecoderEvent headerEvent = DecodeFirstL16Header(path);
        Assert.Equal(SharpMzDecoderEventType.HeaderValid, headerEvent.Type);
        Assert.Equal((ushort)0x01A5, headerEvent.CalculatedChecksum);
        Assert.Equal(headerEvent.CalculatedChecksum, headerEvent.RecordedChecksum);
    }

    [Fact]
    public void Importer_ReadsTurboCopyFullPeriodRegressionWaveform()
    {
        string mzfPath = Path.Combine(AppContext.BaseDirectory, "MZF", "Tc122.mzf");
        string l16Path = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-turbocopy.l16");
        byte[] mzf = File.ReadAllBytes(mzfPath);
        byte[] header = mzf[..128];
        byte[] body = mzf[128..];

        try
        {
            File.WriteAllBytes(l16Path, CreateFullPeriodRegressionL16(header, body));

            TapeRecord record = Assert.Single(SharpTapeImporter.ReadFile(l16Path));

            Assert.Equal((ushort)7083, record.Header.MzfSize);
            Assert.Equal((ushort)0x1200, record.Header.MzfStart);
            Assert.Equal((ushort)0x2D98, record.Header.MzfExec);
            Assert.Equal((ushort)0x6393, ComputeChecksum(record.Body.MzfBody));
            SharpMzDecoderEvent headerEvent = DecodeFirstL16Header(l16Path);
            Assert.Equal((ushort)0x01B7, headerEvent.CalculatedChecksum);
            Assert.Equal(headerEvent.CalculatedChecksum, headerEvent.RecordedChecksum);
        }
        finally
        {
            File.Delete(l16Path);
        }
    }

    [Theory]
    [InlineData(0, ".lep")]
    [InlineData(1, ".l16")]
    [InlineData(2, ".wav")]
    public void Importer_RoundTripsEverySupportedProfile(int formatValue, string extension)
    {
        TapeProfile[] profiles =
        [
            TapeProfile.Normal1_1,
            TapeProfile.Normal1_2,
            TapeProfile.Normal1_3,
            TapeProfile.Normal1_4,
            TapeProfile.Mz700_1_1,
            TapeProfile.Mz700_1_3,
            TapeProfile.Ic1_2,
            TapeProfile.Ic1_3,
            TapeProfile.Ic1_4,
            TapeProfile.Tc1_2,
            TapeProfile.Tc1_3
        ];

        foreach (TapeProfile profile in profiles)
        {
            string path = Path.Combine(
                Path.GetTempPath(),
                $"mztools-{Guid.NewGuid():N}-{profile}{extension}");
            TapeRecord expected = CreateImporterRecord(profile);
            try
            {
                SharpTapeExporter.Export(
                    path,
                    [expected],
                    (SharpTapeOutputFormat)formatValue,
                    SharpTapeMachine.Mz800);

                TapeRecord actual = Assert.Single(SharpTapeImporter.ReadFile(path));

                Assert.Equal(expected.Header.MzfFtype, actual.Header.MzfFtype);
                Assert.Equal(
                    SharpMzEncoding.ConvertMzfNameToASCIIString(expected.Header.MzfFname).TrimEnd(),
                    SharpMzEncoding.ConvertMzfNameToASCIIString(actual.Header.MzfFname).TrimEnd());
                Assert.Equal(expected.Header.MzfSize, actual.Header.MzfSize);
                Assert.Equal(expected.Header.MzfStart, actual.Header.MzfStart);
                Assert.Equal(expected.Header.MzfExec, actual.Header.MzfExec);
                Assert.Equal(expected.Body.MzfBody, actual.Body.MzfBody);
                AssertEquivalentProfile(profile, actual.Profile, (SharpTapeOutputFormat)formatValue);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Importer_MergesAdjacentL16SlotsWithTheSamePhysicalLevel()
    {
        string mzfPath = Path.Combine(AppContext.BaseDirectory, "MZF", "Tc122.mzf");
        string l16Path = Path.Combine(Path.GetTempPath(), $"mztools-{Guid.NewGuid():N}-split-runs.l16");
        byte[] mzf = File.ReadAllBytes(mzfPath);

        try
        {
            File.WriteAllBytes(
                l16Path,
                CreateFullPeriodRegressionL16(mzf[..128], mzf[128..], splitRuns: true));

            TapeRecord record = Assert.Single(SharpTapeImporter.ReadFile(l16Path));

            Assert.Equal((ushort)7083, record.Header.MzfSize);
            Assert.Equal((ushort)0x6393, ComputeChecksum(record.Body.MzfBody));
        }
        finally
        {
            File.Delete(l16Path);
        }
    }

    private static bool DecodesHeader(Timing timing, byte[] header, bool invert)
    {
        var decoder = new SharpMzPulseDecoder();
        var events = new List<SharpMzDecoderEvent>();
        decoder.BeginHeader();
        FeedFramedBlock(decoder, timing, header, events, invert: invert);
        return events.Any(value => value.Type == SharpMzDecoderEventType.HeaderValid);
    }

    private static SharpMzDecoderEvent DecodeFirstL16Header(string path)
    {
        var decoder = new SharpMzPulseDecoder();
        decoder.BeginHeader();
        bool haveInterval = false;
        bool physicalHigh = false;
        long duration = 0;

        foreach (byte raw in File.ReadAllBytes(path))
        {
            int signed = unchecked((sbyte)raw);
            if (signed == 0)
            {
                Assert.True(haveInterval, "The L16 fixture starts with a continuation byte.");
                duration += 127;
                continue;
            }

            bool nextPhysicalHigh = signed > 0;
            int nextDuration = Math.Abs(signed);
            if (!haveInterval)
            {
                haveInterval = true;
                physicalHigh = nextPhysicalHigh;
                duration = nextDuration;
                continue;
            }
            if (nextPhysicalHigh == physicalHigh)
            {
                duration += nextDuration;
                continue;
            }

            if (FeedForHeader(decoder, duration, physicalHigh) is SharpMzDecoderEvent headerEvent)
            {
                return headerEvent;
            }
            physicalHigh = nextPhysicalHigh;
            duration = nextDuration;
        }

        if (haveInterval &&
            FeedForHeader(decoder, duration, physicalHigh) is SharpMzDecoderEvent finalEvent)
        {
            return finalEvent;
        }
        throw new Xunit.Sdk.XunitException("The L16 fixture did not contain a valid header.");
    }

    private static TapeRecord CreateImporterRecord(TapeProfile profile)
    {
        byte[] bodyBytes = [0x10, 0x20, 0x30, 0x40, 0x55, 0xAA];
        byte[] name = new byte[16];
        "ROUNDTRIP"u8.CopyTo(name);
        var header = new MZQFileHeader
        {
            MzfFtype = 0x01,
            MzfFname = name,
            MzfFnameEnd = 0x0D,
            MzfSize = (ushort)bodyBytes.Length,
            MzfStart = 0x1200,
            MzfExec = 0x1200,
            MzfHeaderDescription = new byte[104]
        };
        var body = new MZQFileBody
        {
            DataSize = (ushort)bodyBytes.Length,
            MzfBody = bodyBytes,
            TrailingData = []
        };
        TapeRecord record = TapeRecord.FromLegacy(header, body);
        record.Profile = profile;
        record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
        return record;
    }

    private static void AssertEquivalentProfile(
        TapeProfile expected,
        TapeProfile actual,
        SharpTapeOutputFormat format)
    {
        if (format == SharpTapeOutputFormat.Lep &&
            expected is TapeProfile.Normal1_1 or TapeProfile.Mz700_1_1)
        {
            Assert.Contains(actual, new[] { TapeProfile.Normal1_1, TapeProfile.Mz700_1_1 });
            return;
        }
        if (format == SharpTapeOutputFormat.Wav && expected == TapeProfile.Normal1_3)
        {
            Assert.Contains(actual, new[] { TapeProfile.Normal1_3, TapeProfile.Normal1_4 });
            return;
        }
        Assert.Equal(expected, actual);
    }

    private static SharpMzDecoderEvent? FeedForHeader(
        SharpMzPulseDecoder decoder,
        long duration,
        bool physicalHigh)
    {
        decoder.FeedInterval(duration, physicalHigh);
        while (decoder.TryTakeEvent(out SharpMzDecoderEvent decoderEvent))
        {
            if (decoderEvent.Type == SharpMzDecoderEventType.HeaderValid)
            {
                return decoderEvent;
            }
        }
        return null;
    }

    private static byte[] CreateFullPeriodRegressionL16(
        byte[] header,
        byte[] body,
        bool splitRuns = false)
    {
        // SHORT and LONG have the same 26-unit full period. Only physical LOW
        // (9 versus 18 units) contains the Sharp bit classification.
        var timing = new Timing("full-period regression", 9, 17, 18, 8);
        var bytes = new List<byte>();
        AppendL16Block(bytes, header, timing, leaderPulses: 11000, markPulses: 40, splitRuns);
        AppendL16Block(bytes, body, timing, leaderPulses: 5500, markPulses: 20, splitRuns);
        return bytes.ToArray();
    }

    private static void AppendL16Block(
        List<byte> output,
        byte[] data,
        Timing timing,
        int leaderPulses,
        int markPulses,
        bool splitRuns)
    {
        for (int index = 0; index < leaderPulses; index++)
        {
            AppendL16Pulse(output, timing, isLong: false, splitRuns);
        }
        for (int index = 0; index < markPulses; index++)
        {
            AppendL16Pulse(output, timing, isLong: true, splitRuns);
        }
        for (int index = 0; index < markPulses; index++)
        {
            AppendL16Pulse(output, timing, isLong: false, splitRuns);
        }
        AppendL16Pulse(output, timing, isLong: true, splitRuns);
        AppendL16Pulse(output, timing, isLong: true, splitRuns);
        foreach (byte value in data)
        {
            AppendL16Byte(output, timing, value, splitRuns);
        }
        ushort checksum = ComputeChecksum(data);
        AppendL16Byte(output, timing, (byte)(checksum >> 8), splitRuns);
        AppendL16Byte(output, timing, (byte)checksum, splitRuns);
        AppendL16Pulse(output, timing, isLong: true, splitRuns);
        AppendL16Pulse(output, timing, isLong: true, splitRuns);
    }

    private static void AppendL16Byte(
        List<byte> output,
        Timing timing,
        byte value,
        bool splitRuns)
    {
        for (int mask = 0x80; mask != 0; mask >>= 1)
        {
            AppendL16Pulse(output, timing, (value & mask) != 0, splitRuns);
        }
        AppendL16Pulse(output, timing, isLong: true, splitRuns);
    }

    private static void AppendL16Pulse(
        List<byte> output,
        Timing timing,
        bool isLong,
        bool splitRuns)
    {
        int physicalLow = isLong ? timing.LongPhysicalLow : timing.ShortPhysicalLow;
        int physicalHigh = isLong ? timing.LongPhysicalHigh : timing.ShortPhysicalHigh;
        if (splitRuns)
        {
            int firstLow = physicalLow / 2;
            int firstHigh = physicalHigh / 2;
            output.Add(unchecked((byte)(sbyte)-firstLow));
            output.Add(unchecked((byte)(sbyte)-(physicalLow - firstLow)));
            output.Add((byte)firstHigh);
            output.Add((byte)(physicalHigh - firstHigh));
            return;
        }
        output.Add(unchecked((byte)(sbyte)-physicalLow));
        output.Add((byte)physicalHigh);
    }

    private static byte[] CreateHeader() =>
        Enumerable.Range(0, 128)
            .Select(index => (byte)((index * 37 + 11) & 0xFF))
            .ToArray();

    private static void FeedFramedBlock(
        SharpMzPulseDecoder decoder,
        Timing timing,
        byte[] data,
        List<SharpMzDecoderEvent> events,
        bool corruptChecksum = false,
        bool invert = false)
    {
        FeedLeaderAndMark(decoder, timing, events, invert: invert);
        FeedBytesAndChecksum(decoder, timing, data, events, corruptChecksum, invert);
    }

    private static void FeedLeaderAndMark(
        SharpMzPulseDecoder decoder,
        Timing timing,
        List<SharpMzDecoderEvent> events,
        int leaderPulses = 32,
        int longMarkPulses = 16,
        int shortMarkPulses = 16,
        bool invert = false)
    {
        for (int index = 0; index < leaderPulses; index++)
        {
            FeedPulse(decoder, timing, isLong: false, events, invert);
        }
        for (int index = 0; index < longMarkPulses; index++)
        {
            FeedPulse(decoder, timing, isLong: true, events, invert);
        }
        for (int index = 0; index < shortMarkPulses; index++)
        {
            FeedPulse(decoder, timing, isLong: false, events, invert);
        }
        FeedPulse(decoder, timing, isLong: true, events, invert);
        FeedPulse(decoder, timing, isLong: true, events, invert);
    }

    private static void FeedMarkAndData(
        SharpMzPulseDecoder decoder,
        Timing timing,
        byte[] data,
        List<SharpMzDecoderEvent> events,
        int longMarkPulses,
        int shortMarkPulses)
    {
        for (int index = 0; index < longMarkPulses; index++)
        {
            FeedPulse(decoder, timing, isLong: true, events);
        }
        for (int index = 0; index < shortMarkPulses; index++)
        {
            FeedPulse(decoder, timing, isLong: false, events);
        }
        FeedPulse(decoder, timing, isLong: true, events);
        FeedPulse(decoder, timing, isLong: true, events);
        FeedBytesAndChecksum(decoder, timing, data, events);
    }

    private static void FeedBytesAndChecksum(
        SharpMzPulseDecoder decoder,
        Timing timing,
        byte[] data,
        List<SharpMzDecoderEvent> events,
        bool corruptChecksum = false,
        bool invert = false)
    {
        foreach (byte value in data)
        {
            FeedByte(decoder, timing, value, events, invert);
        }
        ushort checksum = ComputeChecksum(data);
        if (corruptChecksum)
        {
            checksum ^= 1;
        }
        FeedByte(decoder, timing, (byte)(checksum >> 8), events, invert);
        FeedByte(decoder, timing, (byte)checksum, events, invert);
    }

    private static void FeedByte(
        SharpMzPulseDecoder decoder,
        Timing timing,
        byte value,
        List<SharpMzDecoderEvent> events,
        bool invert)
    {
        for (int mask = 0x80; mask != 0; mask >>= 1)
        {
            FeedPulse(decoder, timing, (value & mask) != 0, events, invert);
        }
        FeedPulse(decoder, timing, isLong: true, events, invert);
    }

    private static void FeedPulse(
        SharpMzPulseDecoder decoder,
        Timing timing,
        bool isLong,
        List<SharpMzDecoderEvent> events,
        bool invert = false)
    {
        FeedRawPulse(
            decoder,
            isLong ? timing.LongPhysicalLow : timing.ShortPhysicalLow,
            isLong ? timing.LongPhysicalHigh : timing.ShortPhysicalHigh,
            events,
            invert);
    }

    private static void FeedRawPulse(
        SharpMzPulseDecoder decoder,
        int physicalLow,
        int physicalHigh,
        List<SharpMzDecoderEvent> events,
        bool invert = false)
    {
        FeedInterval(decoder, physicalLow, physicalHigh: invert, events);
        FeedInterval(decoder, physicalHigh, physicalHigh: !invert, events);
    }

    private static void FeedInterval(
        SharpMzPulseDecoder decoder,
        int duration,
        bool physicalHigh,
        List<SharpMzDecoderEvent> events)
    {
        decoder.FeedInterval(duration, physicalHigh);
        while (decoder.TryTakeEvent(out SharpMzDecoderEvent decoderEvent))
        {
            events.Add(decoderEvent);
        }
    }

    private static ushort ComputeChecksum(byte[] data)
    {
        uint checksum = 0;
        foreach (byte value in data)
        {
            checksum += (uint)System.Numerics.BitOperations.PopCount(value);
        }
        return unchecked((ushort)checksum);
    }

    private readonly record struct Timing(
        string Name,
        int ShortPhysicalLow,
        int ShortPhysicalHigh,
        int LongPhysicalLow,
        int LongPhysicalHigh);
}
