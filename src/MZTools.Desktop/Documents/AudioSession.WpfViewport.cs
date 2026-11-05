using System.Globalization;

namespace MZTools.Desktop.Documents;

// WPF parity: applying stereo alignment changes only the presented signal,
// not the user's current zoom, horizontal offset, or selected sample interval.
public sealed partial class AudioSession
{
    internal async Task<bool> ApplyTransformKeepingViewportAsync(CancellationToken cancellation)
    {
        RequireSingleInterval();
        long settingsAtStart = settingsGeneration;
        long selectionAtStart = selectionGeneration;
        long viewportAtStart = viewGeneration;
        long first = ViewStart, last = ViewEnd;
        var transform = RequestedTransform();
        int channel = transform.Mix || ChannelMode != 1 ? 0 : 1;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);

        // The original preview recomputes the selection and then calls ShowRange
        // with selection bounds. Instead, preview the *visible* range and leave
        // ViewStart/ViewEnd and selection properties untouched.
        var result = await Task.Run(() => AudioRangeInspection.Analyze(Model, first, last,
            transform, channel, WavPulseMode.ZeroCrossing, 1, false, null,
            linked.Token, waveformOnly: true), linked.Token);

        if (disposed || settingsAtStart != settingsGeneration ||
            selectionAtStart != selectionGeneration || viewportAtStart != viewGeneration)
            return false;

        AppliedTransform = transform;
        DisplayWaveform = result.Analysis.Waveform;
        DisplayedTransform = transform;
        if(previewBounds is not null)SetRawSelectedPreview(result);
        ClearSpectral();
        settingsDirty = false;
        Validation = "";
        foreach (var property in new[]
        {
            nameof(DisplayWaveform), nameof(HasDraft), nameof(Title),
            nameof(Validation), nameof(TransformDescription)
        }) Changed(property);
        return true;
    }

    internal void StepRightChannel(int frames)
    {
        if (!CanAlign) throw new InvalidOperationException("The source is not stereo.");
        if (!int.TryParse(RightShift, NumberStyles.Integer, CultureInfo.InvariantCulture, out int current))
            throw new InvalidDataException("Enter a valid integer right-channel shift in source frames.");
        RightShift = checked(current + frames).ToString(CultureInfo.InvariantCulture);
    }
}
