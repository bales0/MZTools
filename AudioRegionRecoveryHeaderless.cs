using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MZTools;

internal sealed partial class AudioRegionRecoveryWindow
{
    private readonly ComboBox decodeScope = new() { Width = 210, Margin = new(5) };
    private readonly CheckBox unknownLength = Choice("Unknown length / framing boundaries", false), diagnosticsEnabled = Choice("Recovery diagnostics (bounded)", true);
    private readonly Button syntheticMzf = new() { Content = "Create MZF with supplied metadata…", Margin = new(5), IsEnabled = false };
    private readonly DataGrid recoveryEvents = AudioSignalAnalyzerWindow.Grid();

    private void InitializeHeaderless(StackPanel top, TabControl tabs, WrapPanel footer)
    {
        decodeScope.Items.Add("Standard MZF record"); decodeScope.Items.Add("Payload only (no header)"); decodeScope.SelectedIndex = 0;
        var scope = new WrapPanel(); scope.Children.Add(new TextBlock { Text = "Decode scope", Margin = new(5), VerticalAlignment = VerticalAlignment.Center });
        scope.Children.Add(decodeScope); scope.Children.Add(unknownLength); scope.Children.Add(diagnosticsEnabled);
        top.Children.Insert(2, scope); footer.Children.Insert(3, syntheticMzf);
        void Update()
        {
            unknownLength.IsEnabled = decodeScope.SelectedIndex == 1;
            length.IsEnabled = work == null && !(decodeScope.SelectedIndex == 1 && unknownLength.IsChecked == true);
            RefreshHistogram();
        }
        decodeScope.SelectionChanged += (_, _) => Update(); unknownLength.Click += (_, _) => Update(); Update();
        tabs.Items.Add(new TabItem { Header = "Candidates / Recovery", Content = recoveryEvents });
        recoveryEvents.SelectionChanged += (_, _) =>
        {
            if (recoveryEvents.SelectedItem is AudioRecoveryEvent item) waveform.SetSelection(Math.Max(0, item.Sample - source.SampleRate / 100), Math.Min(source.FrameCount, item.Sample + source.SampleRate / 100));
        };
        recoveryEvents.MouseDoubleClick += (_, _) =>
        {
            if (recoveryEvents.SelectedItem is AudioRecoveryEvent item)
            { waveform.ShowRange(item.Sample - source.SampleRate / 10, item.Sample + source.SampleRate / 10); tabs.SelectedIndex = 0; visualTabs.SelectedIndex = 0; }
        };
        syntheticMzf.Click += (_, _) => CreateSyntheticMzf();
    }
    internal static string CandidateStatus(SharpBlockCandidate c) => !c.ChecksumAvailable ? "Partial / checksum unknown" :
        !c.ChecksumValid ? "Checksum mismatch" : !c.LengthVerified ? "Checksum match / unverified length" : "Verified checksum";

    private void CreateSyntheticMzf()
    {
        if (work != null || Result == null || candidates.SelectedItem == null) return;
        var candidate = Result.Candidates[Number(candidates.SelectedItem)];
        if (candidate.Kind != SharpBlockKind.Payload || !candidate.ChecksumAvailable || !candidate.ChecksumValid) return;
        var dialog = new Window { Owner = this, Title = "Reconstructed MZF — synthetic header", Width = 540, Height = 460, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new StackPanel { Margin = new(15) }; dialog.Content = root;
        root.Children.Add(new TextBlock { Text = $"{candidate.Data.Length:N0} decoded bytes; {CandidateStatus(candidate)}.\nName, type, LOAD and EXEC cannot be recovered from this payload. Supply them explicitly. The original tape header is unavailable.", TextWrapping = TextWrapping.Wrap });
        TextBox Field(string label)
        {
            var row = new DockPanel { Margin = new(0, 5, 0, 0) }; row.Children.Add(new TextBlock { Text = label, Width = 160, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { MinWidth = 230 }; row.Children.Add(box); root.Children.Add(row); return box;
        }
        var name = Field("Sharp filename (≤16)"); var type = Field("Type (hex)"); type.Text = "01";
        var load = Field("LOAD (hex)"); var exec = Field("EXEC (hex)"); var description = Field("Description (optional)");
        var acknowledge = Choice("Use my metadata to create a synthetic header", false); root.Children.Add(acknowledge);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0) }; root.Children.Add(error);
        var save = new Button { Content = "Create MZF…", Margin = new(0, 10, 0, 0), Padding = new(8), IsEnabled = false }; root.Children.Add(save);
        acknowledge.Checked += (_, _) => save.IsEnabled = true; acknowledge.Unchecked += (_, _) => save.IsEnabled = false;
        save.Click += (_, _) =>
        {
            try
            {
                ushort Hex(TextBox box) => ushort.TryParse(box.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value) ? value : throw new ArgumentException("Enter valid hexadecimal type, LOAD and EXEC values.");
                ushort t = Hex(type); if (t > 255) throw new ArgumentException("Type must be 01–FF.");
                var record = AudioSyntheticHeader.Create(candidate.Data, name.Text, (byte)t, Hex(load), Hex(exec), description.Text);
                var target = new SaveFileDialog { Filter = "MZF with synthetic header (*.mzf)|*.mzf", FileName = "reconstructed.mzf", AddExtension = true };
                if (target.ShowDialog(dialog) != true) return;
                TapeDocumentWriter.SaveMzf(target.FileName, record, preserveTrailing: true, createSidecar: false);
                status.Text = "Saved decoded payload with an explicitly supplied synthetic MZF header: " + target.FileName; dialog.Close();
            }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        dialog.ShowDialog();
    }
}
