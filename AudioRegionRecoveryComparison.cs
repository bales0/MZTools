using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MZTools;

internal sealed partial class AudioRegionRecoveryWindow
{
    private readonly ComboBox comparisonLeft = new() { MinWidth = 240, Margin = new(4) }, comparisonRight = new() { MinWidth = 240, Margin = new(4) };
    private readonly DataGrid comparisonRows = AudioSignalAnalyzerWindow.Grid();
    private readonly TextBlock comparisonStatus = new() { Margin = new(5), TextWrapping = System.Windows.TextWrapping.Wrap };
    private AudioPassComparisonResult? passComparison;
    private void InitializeComparison(TabControl tabs)
    {
        var panel = new DockPanel(); var top = new StackPanel(); DockPanel.SetDock(top,Dock.Top); panel.Children.Add(top);
        var controls = new WrapPanel(); top.Children.Add(controls); controls.Children.Add(comparisonLeft); controls.Children.Add(comparisonRight);
        var compare = new Button { Content = "Compare retained passes", Margin = new(4) }; controls.Children.Add(compare);
        var export = new Button { Content = "Export comparison JSON…", Margin = new(4) }; controls.Children.Add(export);
        top.Children.Add(comparisonStatus); panel.Children.Add(comparisonRows);
        tabs.Items.Add(new TabItem { Header = "Pass comparison", Content = panel });
        compare.Click += (_, _) => {
            if (work != null || Result == null || comparisonLeft.SelectedIndex < 0 || comparisonRight.SelectedIndex < 0) return;
            passComparison = AudioDecodePassComparison.Compare(Result,comparisonLeft.SelectedIndex,comparisonRight.SelectedIndex);
            comparisonStatus.Text = passComparison.Status + $" • {passComparison.OmittedDiagnostics} omitted events. Bounded evidence cannot establish an absolute first bad byte.";
            comparisonRows.ItemsSource = passComparison.Rows;
        };
        comparisonRows.MouseDoubleClick += (_, _) => {
            if (comparisonRows.SelectedItem is AudioPassDifference { Sample: long sample }) {
                waveform.SetSelection(Math.Max(0,sample-source.SampleRate/100),Math.Min(source.FrameCount,sample+source.SampleRate/100));
                waveform.ShowRange(Math.Max(0,sample-source.SampleRate/10),Math.Min(source.FrameCount,sample+source.SampleRate/10));
            }
        };
        export.Click += (_, _) => {
            if (passComparison == null) return;
            var dialog = new SaveFileDialog { Filter = "JSON comparison (*.json)|*.json", DefaultExt = ".json", AddExtension = true, FileName = "decode-pass-comparison.json" };
            if (dialog.ShowDialog(this) == true) {
                try { File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(passComparison,new JsonSerializerOptions { WriteIndented = true })); }
                catch (Exception ex) { comparisonStatus.Text = "Cannot save comparison: " + ex.Message; }
            }
        };
    }
    private void RefreshComparison()
    {
        passComparison = null; comparisonRows.ItemsSource = null;
        var choices = Result?.Passes.Select((p,i) => $"{i+1}: {p.Test} • {p.Detectors} ×{p.ThresholdScale:G} • H:{p.ValidHeaders} P:{p.ValidPayloads}").ToArray() ?? [];
        comparisonLeft.ItemsSource = choices; comparisonRight.ItemsSource = choices;
        comparisonLeft.SelectedIndex = choices.Length > 0 ? 0 : -1; comparisonRight.SelectedIndex = choices.Length > 1 ? 1 : comparisonLeft.SelectedIndex;
        comparisonStatus.Text = "Compare retained passes from the latest Deep analysis. Enable Recovery diagnostics before scanning. Histogram selection remains automatic.";
    }
}
