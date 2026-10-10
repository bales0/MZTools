using System;
using System.Collections.Generic;
using System.Linq;

namespace MZTools;

internal sealed record AudioPulseGuide(string Group, double Microseconds, string Context);
internal sealed record AudioPulseReference(string Name, string Source, string Evidence, IReadOnlyList<AudioPulseGuide> Guides)
{
    internal static readonly string[] Groups = ["SHORT High", "SHORT Low", "LONG High", "LONG Low"];
    internal static AudioPulseReference User(string name, double shortHigh, double shortLow, double longHigh, double longLow)
    {
        double[] values = [shortHigh, shortLow, longHigh, longLow];
        if (string.IsNullOrWhiteSpace(name) || values.Any(v => !double.IsFinite(v) || v <= 0 || v > 1_000_000))
            throw new ArgumentException("Enter a name and four positive durations in µs (at most 1,000,000).");
        return new(name.Trim(), "Entered in this analysis window", "USER; entered durations, not independently measured", values.Select((v, i) => new AudioPulseGuide(Groups[i], v, "User value")).ToArray());
    }
}

// References audited against the two specification documents. Comparison alone
// does not change decoding; manual reference tests explicitly opt into these values.
internal static class AudioPulseReferences
{
    private const string Rom = "specification/CMT_TIMING_REFERENCE.md";
    private const string Copier = "specification/CMT_INTERCOPY_TURBOCOPY_TIMING_REFERENCE.md";
    private static AudioPulseReference Fixed(string name, string source, string evidence, double sh, double sl, double lh, double ll) =>
        new(name, source, evidence, new[] { sh, sl, lh, ll }.Select((v, i) => new AudioPulseGuide(AudioPulseReference.Groups[i], v, "Fixed waveform / stated context")).ToArray());
    private static AudioPulseReference Monitor(string name, string section, double sh, double lh, double[] sl, double[] ll) =>
        new(name, Rom + section, "ROM-PATH-HIGH; context-dependent LOW phases", new[] { new AudioPulseGuide("SHORT High", sh, "ROM rising → falling"), new AudioPulseGuide("LONG High", lh, "ROM rising → falling") }
            .Concat(sl.Select((v, i) => new AudioPulseGuide("SHORT Low", v, new[] { "Leader", "Tape mark", "Data S→L", "Data S→S" }[i])))
            .Concat(ll.Select((v, i) => new AudioPulseGuide("LONG Low", v, new[] { "Tape mark", "Data L→L", "Data L→S" }[i]))).ToArray());
    internal static IReadOnlyList<AudioPulseReference> BuiltIn { get; } = new[]
    {
        Monitor("MZ-800 ROM 1Z-013B", "#64-exact-context-dependent-low-widths", 237.956, 469.145, [258.819, 256, 252.617, 255.436], [487.189, 483.806, 486.626]),
        Monitor("MZ-700 ROM 1Z-013A PAL", "#54-exact-context-dependent-mz-700-pulse-table", 242.749, 469.991, [266.714, 263.330, 264.458, 268.123], [490.573, 495.366, 499.031]),
        Monitor("MZ-700 ROM 1Z-009A NTSC", "#54-exact-context-dependent-mz-700-pulse-table", 240.533, 464.025, [264.279, 260.927, 262.044, 265.676], [494.476, 499.225, 502.857]),
        Fixed("MZ-800 service nominal", Rom + "#65-comparison-with-sharp-service-values", "OFFICIAL-HW; nominal, not ROM software edges", 240, 278, 470, 494),
        Fixed("MZ-700 service nominal", Rom + "#55-why-the-current-mz-700-profile-works", "OFFICIAL-HW; nominal", 240, 264, 464, 494),
        Fixed("Intercopy writer 1200 Bd", Copier + "#24-recommended-table-of-authoritative-sources", "COPIER-WRITER-EXACT", 234.573, 263.894, 469.145, 494.802),
        Fixed("Intercopy writer 2400 Bd", Copier + "#24-recommended-table-of-authoritative-sources", "COPIER-WRITER-EXACT", 113.621, 139.278, 234.573, 260.229),
        Fixed("Intercopy writer 2800 Bd (7:3)", Copier + "#24-recommended-table-of-authoritative-sources", "COPIER-WRITER-EXACT; project alias 1:3", 87.965, 124.617, 175.930, 223.577),
        Fixed("Intercopy writer 3200 Bd (8:3)", Copier + "#24-recommended-table-of-authoritative-sources", "COPIER-WRITER-EXACT; project alias 1:4", 76.969, 117.286, 157.604, 179.595),
        // §27: count = floor(480 * 1 / 1) + 71 = 551; MODE3 halves 276/275 ticks.
        Fixed("TurboCopy 1.22 timer OUT0 1:1", Copier + "#27-machine-readable-reference", "COPIER-TIMER-EXACT at nominal CKMS 1.10 MHz; calculated from the documented counter formula; not exact cassette WRITE edges", 250.909, 250, 500.909, 500.909),
        Fixed("TurboCopy 1.22 timer OUT0 1:2", Copier + "#27-machine-readable-reference", "COPIER-TIMER-EXACT at nominal CKMS 1.10 MHz; not exact cassette WRITE edges", 141.818, 140.909, 282.727, 282.727),
        Fixed("TurboCopy 1.22 timer OUT0 1:3", Copier + "#27-machine-readable-reference", "COPIER-TIMER-EXACT at nominal CKMS 1.10 MHz; not exact cassette WRITE edges", 105.455, 104.545, 210, 210),
        Fixed("IC 1:2 compatibility waveform", Copier + "#27-machine-readable-reference", "PROJECT-COMPAT; not receiver decision timing", 144, 112, 256, 224),
        Fixed("IC 1:3 compatibility waveform", Copier + "#27-machine-readable-reference", "PROJECT-COMPAT; not receiver decision timing", 112, 96, 224, 192),
        Fixed("IC 1:4 compatibility waveform", Copier + "#27-machine-readable-reference", "PROJECT-COMPAT; not receiver decision timing", 112, 80, 176, 160),
        Fixed("TC 1:2 compatibility waveform", Copier + "#27-machine-readable-reference", "PROJECT-COMPAT; not timer OUT0 or receiver decision timing", 144, 144, 288, 288),
        Fixed("TC 1:3 compatibility waveform", Copier + "#27-machine-readable-reference", "PROJECT-COMPAT; not timer OUT0 or receiver decision timing", 112, 112, 204, 204),
        Fixed("MZ-700 FAST3 compatibility waveform", Rom + "#93-fast3-current-pwm-and-margins", "PROJECT-COMPAT; not receiver decision timing", 80, 80, 160, 160)
    };
}
