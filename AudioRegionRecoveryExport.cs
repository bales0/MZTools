using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed partial class AudioRegionRecoveryWindow
{
    private readonly Button exportWave = new() { Content = "Export selection to WAV…", Margin = new(5), Padding = new(9, 4, 9, 4), IsEnabled = false,
        ToolTip = "Select audio with Shift+drag. Export uses the current shift, right polarity and Left/Right/Average signal, at the original sample rate and bit depth. All channels exports stereo." };
    private CancellationTokenSource? wavWork;
    private bool previewReady;
    private void UpdateWaveExport()
    {
        exportWave.IsEnabled = !closed && previewReady && preview != null && work == null && wavWork == null && previewWork == null &&
            waveform.SelectionStart.HasValue && waveform.SelectionEnd > waveform.SelectionStart && waveform.SelectionStart >= preview.Start && waveform.SelectionEnd <= preview.End;
    }
    private async Task ChooseWaveExport()
    {
        if (!exportWave.IsEnabled) return;
        var dialog = new SaveFileDialog { Title = $"Export selected waveform — {source.SampleRate:N0} Hz / {source.BitsPerSample}-bit PCM WAV", Filter = "PCM WAV (*.wav)|*.wav", DefaultExt = ".wav", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(source.Source) + "_selection.wav" };
        if (dialog.ShowDialog(this) == true) await ExportSelectionWave(dialog.FileName);
    }
    internal async Task<AudioSelectionWaveExport?> ExportSelectionWave(string path)
    {
        UpdateWaveExport(); if (!exportWave.IsEnabled) return null;
        long first = waveform.SelectionStart!.Value, last = waveform.SelectionEnd!.Value;
        var transform = displayTransform; int mode = waveform.ChannelMode;
        using var cancellation = new CancellationTokenSource(); wavWork = cancellation;
        previewTimer.Stop(); zoomWork?.Cancel(); UpdateWaveExport(); UpdateCandidateActions();
        scan.IsEnabled = from.IsEnabled = to.IsEnabled = length.IsEnabled = saveProgram.IsEnabled = false;
        testSettings.IsEnabled = alignmentSettings.IsEnabled = previewSettings.IsEnabled = waveform.IsEnabled = decodeScope.IsEnabled = unknownLength.IsEnabled = diagnosticsEnabled.IsEnabled = syntheticMzf.IsEnabled = false;
        programs.IsEnabled = candidates.IsEnabled = problems.IsEnabled = false;
        cancel.Content = "Cancel WAV export"; cancel.IsEnabled = true; progress.IsIndeterminate = false; progress.Value = 0;
        try
        {
            var reporter = new Progress<WavAnalysisProgress>(p => { if (!closed && wavWork == cancellation) { status.Text = p.Stage + $" — {p.Fraction:P0}"; progress.Value = p.Fraction; } });
            var result = await Task.Run(() => AudioSelectionWaveExporter.Export(source, path, first, last, transform, mode, reporter, cancellation.Token), cancellation.Token);
            if (!closed) status.Text = $"WAV saved: {path} • samples [{result.StartSample:N0}, {result.EndSample:N0}) • {result.Channels} ch • {result.SampleRate:N0} Hz • PCM {result.BitsPerSample} bit.";
            return result;
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "WAV export cancelled. Previous preview and destination retained."; return null; }
        catch (Exception ex) { if (!closed) status.Text = "Cannot export selected waveform: " + ex.Message; return null; }
        finally
        {
            wavWork = null;
            if (!closed)
            {
                scan.IsEnabled = from.IsEnabled = to.IsEnabled = true; length.IsEnabled = !(decodeScope.SelectedIndex == 1 && unknownLength.IsChecked == true);
                decodeScope.IsEnabled = diagnosticsEnabled.IsEnabled = true; unknownLength.IsEnabled = decodeScope.SelectedIndex == 1;
                testSettings.IsEnabled = alignmentSettings.IsEnabled = previewSettings.IsEnabled = waveform.IsEnabled = true;
                programs.IsEnabled = candidates.IsEnabled = problems.IsEnabled = true;
                cancel.Content = "Cancel analysis"; cancel.IsEnabled = false; saveProgram.IsEnabled = programs.SelectedItem != null;
                UpdateCandidateActions(); UpdateWaveExport();
            }
        }
    }
}
