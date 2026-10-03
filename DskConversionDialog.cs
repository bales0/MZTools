using System;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class DskConversionDialog : Window
{
    private readonly ComboBox profiles = new() { MinWidth = 500, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox report = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button convert = new() { Content = "Convert and Save As...", MinWidth = 170, IsEnabled = false, Margin = new Thickness(8, 10, 0, 0) };
    private DskConversionPreflight? result;

    private DskConversionDialog(Window? owner, DskDocument source)
    {
        Owner = owner; Title = "Convert Format — new image"; Width = 800; Height = 590;
        MinWidth = 640; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 10, 0, 0) };
        buttons.Children.Add(convert); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var label = new TextBlock { Text = "Target format (all operations create a separate DSK):", Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(label, Dock.Top); root.Children.Add(label);
        DockPanel.SetDock(profiles, Dock.Top); root.Children.Add(profiles); root.Children.Add(report); Content = root;
        profiles.ItemsSource = DskCapabilityService.GetAvailableConversions(source);
        profiles.SelectionChanged += (_, _) =>
        {
            convert.IsEnabled = false; result = null;
            if (profiles.SelectedItem is not DskConversionProfile profile) return;
            result = DskFileTransferService.Preflight(source, profile);
            report.Text = result.Report;
            convert.IsEnabled = result.CanConvert;
        };
        convert.Click += (_, _) => { if (result?.CanConvert == true) DialogResult = true; };
        profiles.SelectedIndex = 0;
    }

    internal static DskConversionPreflight? Show(Window owner, DskDocument source)
    {
        var dialog = new DskConversionDialog(owner, source);
        return dialog.ShowDialog() == true ? dialog.result : null;
    }
}
