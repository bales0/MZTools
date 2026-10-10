using System;
using System.Collections.Generic;

namespace MZTools;

internal sealed record AudioRecoveryEvent(string Stage, string Code, string Reason, long Sample, double Seconds,
    int Channel, bool Inverted, string Detector, int ByteIndex, int? ExpectedLength, double PulseSamples, string Context);

internal sealed class AudioRecoveryDiagnostics(bool enabled = true)
{
    private readonly List<AudioRecoveryEvent> events = [];
    private readonly Dictionary<(string, int, bool, string), int> perKind = [];
    internal IReadOnlyList<AudioRecoveryEvent> Events => events;
    internal int Omitted { get; private set; }
    internal string Context { get; set; } = "Initial Zero crossing / Schmitt ×1";
    internal void Add(AudioRecoveryEvent value)
    {
        if (!enabled) return;
        var key = (value.Code, value.Channel, value.Inverted, value.Detector);
        int n = perKind.GetValueOrDefault(key); perKind[key] = n + 1;
        if (events.Count >= 512 || n >= 16) { Omitted++; return; }
        events.Add(value with { Context = Context + "; " + value.Context });
    }
}

internal sealed record SharpPulseDiagnostic(string Code, string State, long Sample, int ByteIndex, int? ExpectedLength,
    double DurationSamples, string Context);
