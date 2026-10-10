using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class CpmLayoutDialog : Window
{
    private readonly Dictionary<string, TextBox> fields = new();
    private readonly CheckBox inverted = new() { Content = "Inverted" };
    private readonly TextBlock blockSize = new();

    internal CpmLayoutDialog(Window owner, DskDocument document)
    {
        Owner = owner; Title = "Attach / Edit CP/M Layout"; Width = 620; Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var preview = new Button { Content = "Validate / Preview...", Margin = new Thickness(6), Padding = new Thickness(10, 4, 10, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(6) };
        actions.Children.Add(preview); actions.Children.Add(cancel); cancel.Click += (_, _) => Close();
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "Interpretation only: applying this layout does not change image bytes.\nThe layout lasts for this open document; reattach it after reopening.\nNumbers are decimal; use 0x for hexadecimal. Physical sectors must be 512 B.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var dpb = (document.FileSystem as CpmFileSystem)?.Dpb ?? CpmDpb.CreateLec(document.Image, false);
        void Field(string label, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = label, Width = 180, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { Text = value }; fields.Add(label, box); row.Children.Add(box); panel.Children.Add(row);
        }
        Field("Name", dpb.Name);
        foreach (var pair in new (string, int)[] { ("SPT", dpb.Spt), ("BSH", dpb.Bsh), ("BLM", dpb.Blm), ("EXM", dpb.Exm), ("DSM", dpb.Dsm), ("DRM", dpb.Drm), ("AL0", dpb.Al0), ("AL1", dpb.Al1), ("CKS", dpb.Cks), ("OFF", dpb.Off) }) Field(pair.Item1, pair.Item2.ToString(CultureInfo.InvariantCulture));
        panel.Children.Add(blockSize);
        fields["BSH"].TextChanged += (_, _) => UpdateBlockSize(); UpdateBlockSize();
        inverted.IsChecked = dpb.Inverted; panel.Children.Add(inverted);
        Field("PhysicalTrackMap", dpb.PhysicalTrackMap == null ? "" : string.Join(",", dpb.PhysicalTrackMap));
        Field("PhysicalSectorMap", dpb.PhysicalSectorMap == null ? "" : string.Join(",", dpb.PhysicalSectorMap.Select(a => $"{a.AbsoluteTrack}:{a.SectorId}")));
        panel.Children.Add(new TextBlock { Text = "Maps use zero-based absolute track indexes. Track map: comma-separated indexes (including OFF system tracks). Sector map: track:sectorID pairs for data sectors. Leave both empty for linear mapping.", TextWrapping = TextWrapping.Wrap });
        CpmDpb ReadLayout()
        {
            ushort U(string name) => checked((ushort)Number(fields[name].Text));
            byte B(string name) => checked((byte)Number(fields[name].Text));
            int bsh = B("BSH");
            if (bsh is < 3 or > 7) throw new ArgumentException("BSH must be 3..7.");
            int[]? tracks = string.IsNullOrWhiteSpace(fields["PhysicalTrackMap"].Text) ? null : Parts(fields["PhysicalTrackMap"].Text).Select(Number).ToArray();
            CpmSectorAddress[]? sectors = string.IsNullOrWhiteSpace(fields["PhysicalSectorMap"].Text) ? null : Parts(fields["PhysicalSectorMap"].Text).Select(value =>
            {
                var parts = value.Split(':');
                if (parts.Length != 2) throw new ArgumentException("Sector map entries must be track:sectorID.");
                return new CpmSectorAddress(Number(parts[0]), Number(parts[1]));
            }).ToArray();
            var proposed = new CpmDpb(U("SPT"), (byte)bsh, B("BLM"), B("EXM"), U("DSM"), U("DRM"), B("AL0"), B("AL1"), U("CKS"), U("OFF"), checked((ushort)(128 << bsh)), fields["Name"].Text, tracks, sectors, inverted.IsChecked == true);
            return proposed;
        }
        var profiles = new Button { Content = "Profiles...", Margin = new Thickness(6) };
        actions.Children.Insert(0, profiles);
        profiles.Click += (_, _) =>
        {
            try { var dialog = new UserProfilesDialog(this, document, currentLayout: ReadLayout()); dialog.ShowDialog(); if (dialog.Applied) { Applied = true; Close(); } }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
        preview.Click += (_, _) =>
        {
            try
            {
                var result = CpmLayoutService.Preview(document, ReadLayout());
                var dialog = new DskPatchPreviewWindow(this, result.Report, result.CanApply ? () => CpmLayoutService.Apply(document, result) : null, "CP/M Layout Preview", "Attach layout");
                dialog.ShowDialog();
                if (dialog.Applied) { Applied = true; Close(); }
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        };
    }
    internal bool Applied { get; private set; }
    private static string[] Parts(string value) => value.Split(',', StringSplitOptions.TrimEntries);
    private static int Number(string value) => value.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.Parse(value.Trim()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : int.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
    private void UpdateBlockSize()
    {
        try { int bsh = Number(fields["BSH"].Text); blockSize.Text = bsh is >= 3 and <= 7 ? $"Derived BlockSize: {128 << bsh} B" : "BSH must be 3..7"; }
        catch (Exception e) when (e is FormatException or OverflowException) { blockSize.Text = "Invalid BSH"; }
    }
}
