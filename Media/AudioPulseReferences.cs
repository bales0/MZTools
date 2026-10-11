using System;
using System.Collections.Generic;
using System.Linq;

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
}
