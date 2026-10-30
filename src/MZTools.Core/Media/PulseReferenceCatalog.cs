using System;
using System.Collections.Generic;
using System.Linq;

namespace MZTools;

internal enum PulseReferenceKind { WriterExact, ReceiverDecision, ServiceNominal, TimerOutput, ProjectCompatibility, Framing, User }
internal sealed record PulseReferenceEntry(string Id, string Machine, string Software, string Stage, PulseReferenceKind Kind,
    string Evidence, string SourceFile, string Section, string Derivation, double? ClockHz,
    IReadOnlyList<AudioPulseGuide> Guides, SharpPulseProfile? Writer = null, double? ReceiverMinUs = null, double? ReceiverMaxUs = null)
{
    public IReadOnlyList<int> RomTStates { get; init; } = [];
    public string SoftwareVersion => Id.StartsWith("ic") || Id.StartsWith("rxic") ? "Intercopy 10.2" : Id.StartsWith("tc") || Id.StartsWith("rxtc") ? "TurboCopy 1.22" : Software;
    public string Polarity => "Logical Sharp 8255 HIGH/LOW; exporter READ connector inversion is separate; timer OUT0 is not cassette WRITE";
    public string SupportedUsages => Kind switch { PulseReferenceKind.ReceiverDecision => "Receiver constraint comparison; never writer pulse duration", PulseReferenceKind.Framing => "Framing only", _ => "Comparison / explicit manual references; writer projection where provided" };
    public string Limitations => Kind switch { PulseReferenceKind.TimerOutput => "Nominal CKMS; Z80 polling edges not reconstructed", PulseReferenceKind.ServiceNominal => "Hardware nominal; not ROM-exact", PulseReferenceKind.ProjectCompatibility => "Existing compatibility values; not historical exact edges", _ => "See source context, evidence and derivation; no hardware certification" };
    internal AudioPulseReference Comparison => new(Software, SourceFile + "#" + Section, Evidence, Guides)
    { CatalogId = Id, Kind = Kind, Machine = Machine, Stage = Stage, Derivation = Derivation };
}

// Versioned projection of the checked-in ROM/copier audits. All historical
// pulse numbers live here; consumers use semantic projections, not new tables.
internal static class PulseReferenceCatalog
{
    internal const string Version = "sharp-mz-pulse-catalog-v1/2026-10-10";
    internal const double PalClockHz = 3_546_875, NtscClockHz = 3_579_545, TcClockMhz = 1.10;
    private const string Rom = "specification/CMT_TIMING_REFERENCE.md", Copier = "specification/CMT_INTERCOPY_TURBOCOPY_TIMING_REFERENCE.md";
    internal const int HeaderLeader = 11000, DataLeader = 5500, HeaderMark = 40, DataMark = 20, FinalMark = 2, Trailing = 2, TcTrailing = 98;
    internal const int UltraHeaderLeader = 2000, UltraDataLeader = 1000;
    internal const int IcDelayMs = 345, TcDelayMs = 110, Fast3DelayMs = 400;
    private static readonly int[] Rom800Ticks = [844, 918, 908, 896, 906, 1664, 1728, 1716, 1726];
    internal static double RomTicks(int ticks) => ticks * 1_000_000.0 / PalClockHz;
    internal static double TcTicks(int ticks) => ticks / TcClockMhz;
    internal static IReadOnlyList<PulseReferenceEntry> Entries { get; } = Build();
    internal static PulseReferenceEntry Get(string id) => Entries.FirstOrDefault(e => e.Id == id) ?? throw new ArgumentException("Unknown pulse reference: " + id);
    internal static IReadOnlyList<AudioPulseReference> Comparisons { get; } = Array.AsReadOnly(Entries.Where(e => e.Guides.Count > 0).Select(e => e.Comparison).ToArray());
    private static IReadOnlyList<PulseReferenceEntry> Build()
    {
        var entries = new List<PulseReferenceEntry>();
        void Monitor(string id, string machine, string name, int[] ticks, double clock, string section)
        {
            string[] groups = ["SHORT High", "SHORT Low", "SHORT Low", "SHORT Low", "SHORT Low", "LONG High", "LONG Low", "LONG Low", "LONG Low"];
            string[] contexts = ["ROM rising → falling", "Leader", "Tape mark", "Data S→L", "Data S→S", "ROM rising → falling", "Tape mark", "Data L→L", "Data L→S"];
            var guides = ticks.Select((t, i) => new AudioPulseGuide(groups[i], Math.Round(t * 1e6 / clock, 3), contexts[i])).ToArray();
            var writer = new SharpPulseProfile(ticks[0] * 1e6 / clock, ticks[4] * 1e6 / clock, ticks[5] * 1e6 / clock, ticks[8] * 1e6 / clock,
                id == "rom800" ? SharpPulseTimingSource.Mz800Rom1Z013B : id == "rom700pal" ? SharpPulseTimingSource.Mz700Rom1Z013APal : SharpPulseTimingSource.Mz700Rom1Z009ANtsc);
            entries.Add(new(id, machine, name, "Header / payload", PulseReferenceKind.WriterExact, "ROM-PATH-HIGH; context-dependent LOW phases", Rom, section,
                "T-states / CPU Hz × 1e6; " + string.Join(",", ticks) + "; LOW context required; comparison rounded to 0.001 µs", clock, Array.AsReadOnly(guides), writer) { RomTStates = Array.AsReadOnly(ticks) });
        }
        Monitor("rom800", "MZ800 PAL", "MZ-800 ROM 1Z-013B", Rom800Ticks, PalClockHz, "64-exact-context-dependent-low-widths");
        Monitor("rom700pal", "MZ700 PAL", "MZ-700 ROM 1Z-013A PAL", [861,946,934,938,951,1667,1740,1757,1770], PalClockHz, "54-exact-context-dependent-mz-700-pulse-table");
        Monitor("rom700ntsc", "MZ700 NTSC", "MZ-700 ROM 1Z-009A NTSC", [861,946,934,938,951,1661,1770,1787,1800], NtscClockHz, "54-exact-context-dependent-mz-700-pulse-table");
        void Fixed(string id, string machine, string name, PulseReferenceKind kind, string evidence, string file, string section, double sh, double sl, double lh, double ll, string formula = "Documented fixed µs values")
        {
            var values = new[] { sh, sl, lh, ll };
            entries.Add(new(id, machine, name, "Header / loader / payload (see profile)", kind, evidence, file, section, formula, formula.Contains("PAL clock") ? PalClockHz : null,
                Array.AsReadOnly(values.Select((v, i) => new AudioPulseGuide(AudioPulseReference.Groups[i], v, "Fixed waveform / stated context")).ToArray()), new(sh, sl, lh, ll)));
        }
        Fixed("service800", "MZ800", "MZ-800 service nominal", PulseReferenceKind.ServiceNominal, "OFFICIAL-HW; nominal, not ROM software edges", Rom, "65-comparison-with-sharp-service-values", 240,278,470,494);
        Fixed("service700", "MZ700", "MZ-700 service nominal", PulseReferenceKind.ServiceNominal, "OFFICIAL-HW; nominal; existing writer compatibility default", Rom, "55-why-the-current-mz-700-profile-works", 240,264,464,494);
        Fixed("ic1200", "MZ800", TapeProfileNames.IcWriterName(TapeProfile.Ic1_1), PulseReferenceKind.WriterExact, "COPIER-WRITER-EXACT", Copier, "61-1200-bd",234.573,263.894,469.145,494.802,"832/936/1664/1755 T at PAL clock; existing writer rounds to 0.001 µs");
        Fixed("ic2400", "MZ800", TapeProfileNames.IcWriterName(TapeProfile.Ic1_2), PulseReferenceKind.WriterExact, "COPIER-WRITER-EXACT", Copier, "62-2400-bd",113.621,139.278,234.573,260.229,"403/494/832/923 T at PAL clock; existing writer rounds to 0.001 µs");
        Fixed("ic2800", "MZ800", TapeProfileNames.IcWriterName(TapeProfile.Ic1_3), PulseReferenceKind.WriterExact, "COPIER-WRITER-EXACT; project alias 1:3", Copier, "63-2800-bd",87.965,124.617,175.930,223.577,"312/442/624/793 T at PAL clock; existing writer rounds to 0.001 µs");
        Fixed("ic3200", "MZ800", TapeProfileNames.IcWriterName(TapeProfile.Ic1_4), PulseReferenceKind.WriterExact, "COPIER-WRITER-EXACT; project alias 1:4", Copier, "64-3200-bd",76.969,117.286,157.604,179.595,"273/416/559/637 T at PAL clock; existing writer rounds to 0.001 µs");
        for(int index=entries.Count-4;index<entries.Count;index++) entries[index]=entries[index] with { Stage=TapeProfileNames.IcWriterStage };
        for (int speed = 1; speed <= 3; speed++)
        {
            int count = 480 / speed + 71;
            var timing = new SharpPulseProfile(TcTicks((count + 1) / 2), TcTicks(count / 2), TcTicks(count), TcTicks(count));
            Fixed("tc" + speed, "MZ800", "TurboCopy 1.22 timer OUT0 1:" + speed, PulseReferenceKind.TimerOutput,
                "COPIER-TIMER-EXACT at nominal CKMS 1.10 MHz; not exact cassette WRITE edges", Copier, "16-turbocopy-v122-mode3-timer-half-periods",
                Math.Round(timing.ShortHighMicroseconds,3), Math.Round(timing.ShortLowMicroseconds,3), Math.Round(timing.LongHighMicroseconds,3), Math.Round(timing.LongLowMicroseconds,3),
                "SHORT_COUNT=floor(480/ratio)+71; MODE3 ceil/floor halves; LONG_COUNT=2*SHORT_COUNT; polling ≈25 PAL T");
            entries[^1] = entries[^1] with { ClockHz = TcClockMhz * 1e6, Writer = timing };
        }
        Fixed("tccompat2", "MZ800", "TC 1:2 compatibility waveform", PulseReferenceKind.ProjectCompatibility,"PROJECT-COMPAT; not timer OUT0 or receiver decision timing",Copier,"19-turbocopy-current-project-waveform-vs-writer-reference",144,144,288,288);
        Fixed("tccompat3", "MZ800", "TC 1:3 compatibility waveform", PulseReferenceKind.ProjectCompatibility,"PROJECT-COMPAT; not timer OUT0 or receiver decision timing",Copier,"19-turbocopy-current-project-waveform-vs-writer-reference",112,112,204,204);
        Fixed("fast3", "MZ700", "MZ-700 FAST3 compatibility waveform", PulseReferenceKind.ProjectCompatibility,"PROJECT-COMPAT; not receiver decision timing",Rom,"93-fast3-current-pwm-and-margins",80,80,160,160);
        void Receiver(string id, string machine, int earliest, int latest, double clock, string section, string formula)
            => entries.Add(new(id, machine, id, "Receiver", PulseReferenceKind.ReceiverDecision,"RECEIVER-EXACT / static polling model",Rom,section,formula,clock,[],null,earliest*1e6/clock,latest*1e6/clock));
        Receiver("rx800", "MZ800",1233,1285,PalClockHz,"66-native-mz-800-receive-decision-window","85+14*82; poll +52 T");
        Receiver("rx700pal", "MZ700 PAL",1237,1299,PalClockHz,"57-mz-700-normal-receive-decision-timing","103+18*63; poll +62 T");
        Receiver("rx700ntsc", "MZ700 NTSC",1345,1407,NtscClockHz,"57-mz-700-normal-receive-decision-timing","103+18*69; poll +62 T");
        foreach (var pair in new[] { (2,32), (3,22), (4,17) }) Receiver("rxic"+pair.Item1,"MZ800",171+14*pair.Item2,223+14*pair.Item2,PalClockHz,"74-ic-receiver-windows-and-current-pwm","171+14*N; poll +52 T; IC EDGE +86 T");
        foreach (var pair in new[] { (2,41), (3,27) }) Receiver("rxtc"+pair.Item1,"MZ800",85+14*pair.Item2,137+14*pair.Item2,PalClockHz,"83-tc-receiver-windows-and-current-pwm","85+14*N; poll +52 T; no IC hook");
        Receiver("rxfast3","MZ700 NTSC",379,431,NtscClockHz,"92-fast3-exact-receiver-equation","85+14*21; poll +52 T");
        entries.Add(new("framing", "MZ800 / MZ700", "MZTools existing framing", "Header / loader / payload", PulseReferenceKind.Framing,"PROJECT-FRAMING; preserves MZTools default",Copier,"21-leaderframing-policy--use-the-separate-document","Header11000/Data5500 (existing MZTools default); marks40/20; final2; trailing2/TC98; native policy22000/11000 is distinct",null,[]));
        Validate(entries);
        return entries.AsReadOnly();
    }
    internal static void Validate(IEnumerable<PulseReferenceEntry> entries)
    {
        var ids = new HashSet<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id) || !Enum.IsDefined(entry.Kind) || string.IsNullOrWhiteSpace(entry.Machine) || string.IsNullOrWhiteSpace(entry.Software) || string.IsNullOrWhiteSpace(entry.Stage) || string.IsNullOrWhiteSpace(entry.Evidence) || string.IsNullOrWhiteSpace(entry.SourceFile) || string.IsNullOrWhiteSpace(entry.Section) || string.IsNullOrWhiteSpace(entry.Derivation)) throw new ArgumentException("Missing provenance or duplicate pulse ID: " + entry.Id);
            if (entry.Guides.Any(g => !AudioPulseReference.Groups.Contains(g.Group) || !double.IsFinite(g.Microseconds) || g.Microseconds <= 0 || string.IsNullOrWhiteSpace(g.Context))) throw new ArgumentException("Invalid pulse group/unit/context: " + entry.Id);
            if (entry.ClockHz.HasValue && (!double.IsFinite(entry.ClockHz.Value) || entry.ClockHz <= 0)) throw new ArgumentException("Invalid clock: " + entry.Id);
            if (entry.Writer is SharpPulseProfile w && new[] { w.ShortHighMicroseconds,w.ShortLowMicroseconds,w.LongHighMicroseconds,w.LongLowMicroseconds }.Any(v => !double.IsFinite(v) || v <= 0)) throw new ArgumentException("Invalid writer duration: " + entry.Id);
            if (entry.Guides.Count > 0 && AudioPulseReference.Groups.Any(group => !entry.Guides.Any(g => g.Group == group))) throw new ArgumentException("Incomplete pulse groups: " + entry.Id);
            if (entry.Kind == PulseReferenceKind.ReceiverDecision && !(entry.ReceiverMaxUs > entry.ReceiverMinUs && entry.ReceiverMinUs > 0)) throw new ArgumentException("Invalid receiver window: " + entry.Id);
        }
    }
    internal static SharpPulseProfile GetWriterTiming(string id) => Get(id).Kind is PulseReferenceKind.ReceiverDecision or PulseReferenceKind.Framing
        ? throw new ArgumentException("Reference is not a writer waveform: " + id) : Get(id).Writer ?? throw new ArgumentException("No writer timing: " + id);
    internal static double GetRomLow(string id, bool isLong, string context, bool nextLong)
    {
        var entry = Get(id);
        if (entry.RomTStates.Count != 9 || !entry.ClockHz.HasValue) throw new ArgumentException("Reference has no ROM LOW contexts: " + id);
        return entry.RomTStates[context switch {
        "Leader" when !isLong => 1, "TapeMark" => isLong ? 6 : 2,
        "Data" => isLong ? nextLong ? 7 : 8 : nextLong ? 3 : 4,
        _ => throw new ArgumentException("Unsupported ROM LOW context") }] * 1e6 / entry.ClockHz!.Value;
    }
    internal static double Rom800Low(bool isLong, string context, bool nextLong) => GetRomLow("rom800",isLong,context,nextLong);
    internal static string ProfileId(TapeProfile p) => p switch {
        TapeProfile.Normal1_2 or TapeProfile.Ic1_2 => "ic2400", TapeProfile.Normal1_3 or TapeProfile.Ic1_3 => "ic2800", TapeProfile.Normal1_4 or TapeProfile.Ic1_4 => "ic3200",
        TapeProfile.Ic1_1 => "ic1200", TapeProfile.Tc1_1 => "tc1", TapeProfile.Tc1_2 => "tc2", TapeProfile.Tc1_3 => "tc3",
        TapeProfile.Mz700_1_1 or TapeProfile.UltraMz700 => "service700", TapeProfile.Mz700_1_3 => "fast3", _ => "rom800" };
    internal static SharpZ80TimingReference Decoder(string name, TapeProfile profile, string origin, int header, int data, string? catalogId = null)
    {
        var entry = Get(catalogId ?? ProfileId(profile));
        double Value(string group) => entry.Guides.First(g => g.Group == group).Microseconds;
        return new(name, profile, Value("SHORT High"), Value("SHORT Low"), profile is TapeProfile.UltraMz700 or TapeProfile.UltraMz800 ? double.NaN : Value("LONG High"),
            profile is TapeProfile.UltraMz700 or TapeProfile.UltraMz800 ? double.NaN : Value("LONG Low"), header, data, origin);
    }
}
