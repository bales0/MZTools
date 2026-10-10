using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace MZTools;

internal sealed partial class AudioSignalAnalyzerWindow
{
    private readonly Button exportWave = new() { Content = "Export selection to WAV…", Margin = new(4), IsEnabled = false,
        Padding = new(10, 4, 10, 4), ToolTip = "Export the continuous highlighted waveform interval at the source sample rate and bit depth. Left/Right exports mono; Overlay L/R preserves stereo. Select audio with Shift+drag." };
    private CancellationTokenSource? wavWork;

    private void UpdateWaveExport() => exportWave.IsEnabled = !closed && analysis != null && work == null && wavWork == null &&
        waveform.SelectionStart.HasValue && waveform.SelectionEnd > waveform.SelectionStart;

    private async Task ChooseWaveExport()
    {
        UpdateWaveExport(); if (!exportWave.IsEnabled || analysis == null) return;
        var dialog = new SaveFileDialog { Title = $"Export highlighted interval — {analysis.SampleRate:N0} Hz / {analysis.BitsPerSample}-bit PCM WAV",
            Filter = "PCM WAV (*.wav)|*.wav", DefaultExt = ".wav", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(analysis.Source) + "_selection.wav" };
        if (dialog.ShowDialog(this) == true) await ExportSelectionWave(dialog.FileName);
    }

    internal async Task<AudioSelectionWaveExport?> ExportSelectionWave(string path)
    {
        UpdateWaveExport(); if (!exportWave.IsEnabled || analysis == null) return null;
        var source = analysis;
        long first = waveform.SelectionStart!.Value, last = waveform.SelectionEnd!.Value;
        int channelMode = Math.Max(0, channelChoice.SelectedIndex);
        using var cancellation = new CancellationTokenSource(); wavWork = cancellation;
        UpdateWaveExport(); open.IsEnabled = report.IsEnabled = deepAnalysis.IsEnabled = channelChoice.IsEnabled = waveform.IsEnabled = tabs.IsEnabled = false;
        cancel.Content = "Cancel WAV export"; cancel.IsEnabled = true; progress.Value = 0;
        try
        {
            var reporter = new Progress<WavAnalysisProgress>(p => { if (!closed && wavWork == cancellation) { status.Text = p.Stage + $" — {p.Fraction:P0}"; progress.Value = p.Fraction; } });
            var result = await Task.Run(() => AudioSelectionWaveExporter.Export(source, path, first, last, new(), channelMode, reporter, cancellation.Token), cancellation.Token);
            if (!closed) status.Text = $"WAV saved: {path} • samples [{result.StartSample:N0}, {result.EndSample:N0}) • {result.Channels} ch • {result.SampleRate:N0} Hz • PCM {result.BitsPerSample} bit.";
            return result;
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "WAV export cancelled. Destination retained."; return null; }
        catch (Exception ex) { if (!closed) status.Text = "Cannot export selected waveform: " + ex.Message; return null; }
        finally
        {
            wavWork = null;
            if (!closed)
            {
                open.IsEnabled = report.IsEnabled = deepAnalysis.IsEnabled = waveform.IsEnabled = tabs.IsEnabled = true;
                channelChoice.IsEnabled = source.ChannelCount > 1;
                cancel.Content = "Cancel analysis"; cancel.IsEnabled = false; UpdateWaveExport();
            }
        }
    }
}
