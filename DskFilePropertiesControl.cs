using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class DskFilePropertiesControl : UserControl
{
    private readonly DskFileEntry entry;
    private readonly DskFilePropertyKind kind;
    private readonly TextBox user = new();
    private readonly TextBox load = new();
    private readonly TextBox execute = new();
    private readonly CheckBox readOnly = new() { Content = "Read-only (RO)" };
    private readonly CheckBox system = new() { Content = "System (SYS)" };
    private readonly CheckBox archived = new() { Content = "Archived (ARC)" };

    internal DskFilePropertiesControl(DskFileEntry entry, DskFilePropertyKind kind)
    {
        this.entry = entry; this.kind = kind;
        var panel = new StackPanel(); Content = panel;
        panel.Children.Add(new TextBlock { Text = $"{entry.Name}.{entry.Extension}\nSize: {entry.Size:N0} B | Blocks: {entry.Blocks} | Extents: {entry.Extents}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        void Row(string label, Control input)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 3) });
            panel.Children.Add(input);
        }
        if (kind == DskFilePropertyKind.Cpm)
        {
            user.Text = entry.User.ToString(CultureInfo.InvariantCulture);
            readOnly.IsChecked = entry.ReadOnly; system.IsChecked = entry.System; archived.IsChecked = entry.Archived;
            Row("User area (0–15)", user); panel.Children.Add(readOnly); panel.Children.Add(system); panel.Children.Add(archived);
        }
        else if (kind == DskFilePropertyKind.Mrs)
        {
            load.Text = entry.LoadAddress.ToString("X4"); execute.Text = entry.ExecuteAddress.ToString("X4");
            Row("LOAD (hex)", load); Row("EXEC (hex)", execute);
        }
    }

    internal DskFileProperties ReadValues()
    {
        var values = DskFileProperties.From(entry);
        if (kind == DskFilePropertyKind.Cpm)
        {
            if (!int.TryParse(user.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int area) || area is < 0 or > 15)
                throw new ArgumentException("User area must be a decimal number from 0 to 15.");
            return values with { User = area, ReadOnly = readOnly.IsChecked == true, System = system.IsChecked == true, Archived = archived.IsChecked == true };
        }
        ushort Hex(string text)
        {
            text = text.Trim(); if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            if (text.Length is < 1 or > 4 || !ushort.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort value))
                throw new ArgumentException("LOAD/EXEC must be hexadecimal addresses from 0000 to FFFF.");
            return value;
        }
        if (kind != DskFilePropertyKind.Mrs) throw new InvalidOperationException("No editable properties for this filesystem.");
        return values with { Load = Hex(load.Text), Execute = Hex(execute.Text) };
    }
}
