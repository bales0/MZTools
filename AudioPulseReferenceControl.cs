using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MZTools;

internal sealed class AudioPulseReferenceControl : Expander
{
    private readonly ObservableCollection<AudioPulseReference> references = new(AudioPulseReferences.BuiltIn);
    private readonly ComboBox choice = new() { DisplayMemberPath = "Name", MinWidth = 260, Margin = new(4) };
    private readonly CheckBox visible = new() { Content = "Show reference grid", IsChecked = true, Margin = new(5), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox follow = new() { Content = "Follow selected block", IsChecked = true, Margin = new(5), VerticalAlignment = VerticalAlignment.Center };
    private bool automaticSelection;
    private AudioBlockAnalysis? block;
    private readonly TextBlock source = new() { TextWrapping = TextWrapping.Wrap, Margin = new(5) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new(5) };
    private readonly TextBox name = new() { Text = "My reference", Width = 150, Margin = new(4) };
    private readonly TextBox[] values = AudioPulseReference.Groups.Select(_ => new TextBox { Width = 75, Margin = new(4) }).ToArray();
    private readonly DataGrid guides = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, Height = 115, Margin = new(5) };
    internal ObservableCollection<AudioPulseReference> References => references;
    internal AudioPulseReference? Selected => choice.SelectedItem as AudioPulseReference;
    internal bool ShowGrid => visible.IsChecked == true;
    internal event Action? Changed;
    internal AudioPulseReferenceControl()
    {
        Header = "Pulse references / user values (µs)"; Margin = new(4);
        var panel = new StackPanel(); Content = panel;
        var selection = new WrapPanel(); panel.Children.Add(selection); selection.Children.Add(visible); selection.Children.Add(follow); selection.Children.Add(choice);
        choice.ItemsSource = references;
        panel.Children.Add(source);
        guides.Columns.Add(new DataGridTextColumn { Header = "Pulse", Binding = new Binding("Group"), Width = 110 });
        guides.Columns.Add(new DataGridTextColumn { Header = "µs", Binding = new Binding("Microseconds") { StringFormat = "0.###" }, Width = 90 });
        guides.Columns.Add(new DataGridTextColumn { Header = "Context", Binding = new Binding("Context"), Width = new(1, DataGridLengthUnitType.Star) });
        panel.Children.Add(guides);
        panel.Children.Add(new TextBlock { Text = "Positive half-cycle durations in µs. Values remain in this window. Comparison guides affect decoding only when explicitly selected for reference tests in deeper analysis.", TextWrapping = TextWrapping.Wrap, Margin = new(5) });
        var inputs = new WrapPanel(); panel.Children.Add(inputs); inputs.Children.Add(name);
        for (int i = 0; i < values.Length; i++)
        {
            inputs.Children.Add(new TextBlock { Text = AudioPulseReference.Groups[i], VerticalAlignment = VerticalAlignment.Center, Margin = new(4) });
            inputs.Children.Add(values[i]);
        }
        var add = new Button { Content = "Add user reference", Margin = new(4), Padding = new(7, 3, 7, 3) }; inputs.Children.Add(add);
        panel.Children.Add(error);
        choice.SelectionChanged += (_, _) =>
        {
            if (Selected is not { } selected) return;
            if (!automaticSelection) follow.IsChecked = false;
            Header = "Pulse references / user values (µs) — " + selected.Name;
            source.Text = selected.Evidence + "\n" + selected.Source;
            guides.ItemsSource = selected.Guides;
            for (int i = 0; i < values.Length; i++) values[i].Text = selected.Guides.FirstOrDefault(g => g.Group == AudioPulseReference.Groups[i])?.Microseconds.ToString("0.###", CultureInfo.CurrentCulture) ?? "";
            Changed?.Invoke();
        };
        visible.Checked += (_, _) => Changed?.Invoke(); visible.Unchecked += (_, _) => Changed?.Invoke();
        follow.Checked += (_, _) => FollowBlock(block);
        add.Click += (_, _) =>
        {
            try { AddUser(name.Text, values.Select(v => v.Text).ToArray()); error.Text = "User reference added. Select it for reference tests to use it in deeper analysis."; }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        automaticSelection = true; choice.SelectedIndex = 0; automaticSelection = false;
    }
    internal void FollowBlock(AudioBlockAnalysis? selectedBlock)
    {
        block = selectedBlock;
        if (follow.IsChecked != true || block == null) return;
        var available = block.Distributions.Where(d => d.Reference is > 0).ToArray();
        if (available.Length == 0) return;
        double Difference(AudioPulseReference reference) => available.Sum(d => reference.Guides.Where(g => g.Group == d.Group)
            .Select(g => Math.Abs(g.Microseconds / d.Reference!.Value - 1)).DefaultIfEmpty(10).Min());
        var match = AudioPulseReferences.BuiltIn.MinBy(Difference);
        automaticSelection = true;
        try { choice.SelectedItem = match; }
        finally { automaticSelection = false; }
    }
    internal AudioPulseReference AddUser(string label, string[] textValues)
    {
        if (textValues.Length != 4) throw new ArgumentException("Enter all four pulse durations.");
        double[] durations = textValues.Select(text => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN).ToArray();
        var reference = AudioPulseReference.User(label, durations[0], durations[1], durations[2], durations[3]);
        references.Add(reference); choice.SelectedItem = reference; return reference;
    }
    internal void UseReference(AudioPulseReference? reference, bool show)
    {
        visible.IsChecked = show;
        if (reference == null) return;
        var existing = references.FirstOrDefault(r => r == reference);
        if (existing == null) { references.Add(reference); existing = reference; }
        choice.SelectedItem = existing;
    }
}
