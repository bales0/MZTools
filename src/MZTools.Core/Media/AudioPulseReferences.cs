using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace MZTools;

internal sealed record AudioPulseGuide(string Group, double Microseconds, string Context);
internal sealed record AudioPulseReference(string Name, string Source, string Evidence, IReadOnlyList<AudioPulseGuide> Guides)
{
    public string? CatalogId { get; init; }
    public PulseReferenceKind Kind { get; init; } = PulseReferenceKind.User;
    public string Machine { get; init; } = "User";
    public string Stage { get; init; } = "Comparison / explicitly selected manual test";
    public string Derivation { get; init; } = "Session-local user values";
    internal static readonly string[] Groups = ["SHORT High", "SHORT Low", "LONG High", "LONG Low"];
    internal static AudioPulseReference User(string name, double shortHigh, double shortLow, double longHigh, double longLow)
    {
        double[] values = [shortHigh, shortLow, longHigh, longLow];
        if (string.IsNullOrWhiteSpace(name) || values.Any(v => !double.IsFinite(v) || v <= 0 || v > 1_000_000))
            throw new ArgumentException("Enter a name and four positive durations in µs (at most 1,000,000).");
        return new(name.Trim(), "Entered in this analysis window", "USER; entered durations, not independently measured", values.Select((v, i) => new AudioPulseGuide(Groups[i], v, "User value")).ToArray());
    }
}

// Compatibility facade; the catalog owns all historical values and provenance.
internal static class AudioPulseReferences
{
    internal static IReadOnlyList<AudioPulseReference> BuiltIn => PulseReferenceCatalog.Comparisons;
    internal static IReadOnlyList<AudioPulseReference> Choices { get; }=BuildChoices();
    private static IReadOnlyList<AudioPulseReference> BuildChoices()
    {
        var choices=new List<AudioPulseReference>();
        foreach(var (id,name) in new[]{("rom800","MZ-800 ROM"),("rom700pal","MZ-700 ROM (PAL)"),("rom700ntsc","MZ-700 ROM (NTSC)")})
            choices.Add(PulseReferenceCatalog.Get(id).Comparison with { Name=name });
        foreach(var profile in new[]{TapeProfile.Normal1_2,TapeProfile.Normal1_3,TapeProfile.Normal1_4,TapeProfile.Ic1_2,TapeProfile.Ic1_3,TapeProfile.Ic1_4,TapeProfile.Tc1_2,TapeProfile.Tc1_3,TapeProfile.Mz700_1_3})
        {
            var reference=PulseReferenceCatalog.Get(PulseReferenceCatalog.ProfileId(profile)).Comparison;
            choices.Add(reference with { Name=TapeProfileNames.ComparisonName(profile),Stage=TapeProfileNames.Description(profile) });
        }
        return choices.AsReadOnly();
    }
    // Classification uses pulse values/groups, not aliases or explanatory contexts.
    internal static string MeasurementKey(AudioPulseReference reference)=>string.Join(";",reference.Guides.Select(g=>g.Group+":"+g.Microseconds.ToString("R",CultureInfo.InvariantCulture)).Distinct().OrderBy(s=>s,StringComparer.Ordinal));
    internal static IEnumerable<AudioPulseReference> UniqueMeasurements(IEnumerable<AudioPulseReference> references)=>references.DistinctBy(MeasurementKey);
}
