using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MZTools;

internal sealed class DskPhysicalPropertiesDialog : Window
{
    public sealed class SectorRow
    {
        public int SourceIndex { get; init; }
        public int SectorId { get; set; }
        public int St1 { get; set; }
        public int St2 { get; set; }
    }
    internal bool Applied { get; private set; }
    internal DskPhysicalPropertiesDialog(Window owner, DskDocument document)
    {
        Owner = owner; Title = "Physical Properties"; Width = 700; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var preview = new Button { Content = "Preview...", Margin = new Thickness(6) }; actions.Children.Add(preview);
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(6) }; actions.Children.Add(close); close.Click += (_, _) => Close();
        var fields = new StackPanel(); DockPanel.SetDock(fields, Dock.Top); root.Children.Add(fields);
        fields.Children.Add(new TextBlock { Text = "Numeric fields are decimal. Order uses source descriptor indexes, not sector IDs.\nChanges are applied to a clone and checked against file contents and Analyzer.", TextWrapping = TextWrapping.Wrap });
        TextBox Field(string name, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) }; fields.Children.Add(row);
            row.Children.Add(new TextBlock { Text = name, Width = 130 }); var box = new TextBox { Text = value }; row.Children.Add(box); return box;
        }
        var creator = Field("Creator", document.Image.Creator);
        var tracks = new ComboBox { ItemsSource = document.Image.Tracks.Select((t, i) => (t, i)).Where(p => p.t != null).Select(p => p.i).ToArray(), SelectedIndex = 0 };
        fields.Children.Add(new TextBlock { Text = "Physical track index" }); fields.Children.Add(tracks);
        var cylinder = Field("Track C", ""); var side = Field("Track H", ""); var gap = Field("GAP#3", ""); var filler = Field("Filler", ""); var order = Field("Descriptor order", "");
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, Margin = new Thickness(0, 8, 0, 0) }; root.Children.Add(grid);
        foreach (string property in new[] { "SourceIndex", "SectorId", "St1", "St2" }) grid.Columns.Add(new DataGridTextColumn { Header = property, Binding = new Binding(property), IsReadOnly = property == "SourceIndex", Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        List<SectorRow> rows = new();
        void Load()
        {
            if (tracks.SelectedItem is not int index) return;
            var track = document.Image.Tracks[index]!;
            cylinder.Text = track.Cylinder.ToString(); side.Text = track.Side.ToString(); gap.Text = track.Gap.ToString(); filler.Text = track.Filler.ToString();
            order.Text = string.Join(",", Enumerable.Range(0, track.Sectors.Count));
            rows = track.Sectors.Select((s, i) => new SectorRow { SourceIndex = i, SectorId = s.SectorId, St1 = s.FdcStatus1, St2 = s.FdcStatus2 }).ToList(); grid.ItemsSource = rows;
        }
        tracks.SelectionChanged += (_, _) => Load(); Load();
        preview.Click += (_, _) =>
        {
            try
            {
                if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) throw new ArgumentException("Correct the sector fields before preview.");
                byte B(TextBox box) => byte.Parse(box.Text, CultureInfo.InvariantCulture);
                var selected = order.Text.Split(',', StringSplitOptions.TrimEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (!selected.Order().SequenceEqual(Enumerable.Range(0, rows.Count))) throw new ArgumentException("Order must contain every source index exactly once.");
                var properties = new DskTrackProperties((int)tracks.SelectedItem, B(cylinder), B(side), B(gap), B(filler), selected.Select(i => new DskSectorProperties(i, checked((byte)rows[i].SectorId), checked((byte)rows[i].St1), checked((byte)rows[i].St2))).ToArray());
                var result = DskPhysicalPropertyService.Preview(document, creator.Text, properties);
                var dialog = new DskPatchPreviewWindow(this, result.Report, () => result.Apply(document), "Physical Properties Preview", "Apply");
                dialog.ShowDialog(); if (dialog.Applied) { Applied = true; Close(); }
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        };
    }
}
