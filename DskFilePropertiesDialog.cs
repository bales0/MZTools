using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class DskFilePropertiesDialog : Window
{
    private readonly TextBox user = new() { MinWidth = 120 };
    private readonly CheckBox readOnly = new() { Content = "Read-only (RO)" };
    private readonly CheckBox system = new() { Content = "System (SYS)" };
    private readonly CheckBox archived = new() { Content = "Archived (ARC)" };
    private readonly TextBox load = new() { MinWidth = 120 };
    private readonly TextBox execute = new() { MinWidth = 120 };
    private readonly TextBox fileType = new() { MinWidth = 120 };
    private readonly CheckBox locked = new() { Content = "Locked" };
    private readonly TextBlock error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly DskFilePropertyKind kind;
    private readonly DskFileEntry entry;
    private DskFileProperties? result;

    private DskFilePropertiesDialog(Window? owner, DskFilePropertyKind kind, DskFileEntry entry)
    {
        if (kind == DskFilePropertyKind.None) throw new ArgumentException("No native editable properties.");
        this.kind = kind; this.entry = entry;
        Owner = owner; Title = "File properties"; Width = 480; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = entry.Name + (entry.Extension.Length == 0 ? "" : "." + entry.Extension), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"Size: {entry.Size:N0} B | Blocks: {entry.Blocks} | Extents: {entry.Extents}", Margin = new Thickness(0, 6, 0, 10) });
        void Row(string label, FrameworkElement value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Width = 170, VerticalAlignment = VerticalAlignment.Center }); row.Children.Add(value); panel.Children.Add(row);
        }
        user.Text = entry.User.ToString(CultureInfo.InvariantCulture);
        readOnly.IsChecked = entry.ReadOnly; system.IsChecked = entry.System; archived.IsChecked = entry.Archived;
        load.Text = entry.LoadAddress.ToString("X4"); execute.Text = entry.ExecuteAddress.ToString("X4");
        if (kind == DskFilePropertyKind.Cpm)
        {
            Row("User area (decimal 0–15):", user); Row("Attributes:", readOnly); Row("", system); Row("", archived);
            panel.Children.Add(new TextBlock { Text = "All extents are updated together. Names, RC, extent numbering and allocation stay unchanged. A user/name collision is refused.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        }
        else
        {
            Row("LOAD (hex 0000–FFFF):", load); Row("EXEC (hex 0000–FFFF):", execute);
            if (kind == DskFilePropertyKind.Fsmz)
            {
                fileType.Text = entry.FileType.ToString("X2"); locked.IsChecked = entry.Locked;
                Row("File type (hex):", fileType); Row("", locked);
            }
            panel.Children.Add(new TextBlock { Text = "Metadata is changed transactionally. File payloads and allocation remain unchanged.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        }
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var apply = new Button { Content = "Apply", MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        apply.Click += Apply_Click; buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons);
    }

    internal DskFileProperties ReadValues()
    {
        var initial = DskFileProperties.From(entry);
        if (kind == DskFilePropertyKind.Cpm)
        {
            if (!int.TryParse(user.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int area) || area is < 0 or > 15)
                throw new ArgumentException("User area must be a decimal number from 0 to 15.");
            return initial with { User = area, ReadOnly = readOnly.IsChecked == true, System = system.IsChecked == true, Archived = archived.IsChecked == true };
        }
        ushort Hex(string value)
        {
            value = value.Trim(); if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            if (value.Length is < 1 or > 4 || !ushort.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort address))
                throw new ArgumentException("LOAD/EXEC must be hexadecimal addresses from 0000 to FFFF.");
            return address;
        }
        return initial with { Load = Hex(load.Text), Execute = Hex(execute.Text),
            FileType = kind == DskFilePropertyKind.Fsmz ? checked((byte)Hex(fileType.Text)) : initial.FileType,
            Locked = kind == DskFilePropertyKind.Fsmz ? locked.IsChecked == true : initial.Locked };
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try { result = ReadValues(); DialogResult = true; }
        catch (ArgumentException ex) { error.Text = ex.Message; }
    }

    internal static DskFileProperties? Show(Window owner, DskFilePropertyKind kind, DskFileEntry entry)
    {
        var dialog = new DskFilePropertiesDialog(owner, kind, entry);
        return dialog.ShowDialog() == true ? dialog.result : null;
    }
}
