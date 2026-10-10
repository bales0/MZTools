using System;
using System.Linq;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class DskConversionDialog : Window
{
    private readonly ComboBox profiles = new() { MinWidth = 500, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox report = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button convert = new() { Content = "Convert and Save As...", MinWidth = 170, IsEnabled = false, Margin = new Thickness(8, 10, 0, 0) };
    private DskConversionPreflight? result;
    private MediaConversionPreview? physicalResult;

    private DskConversionDialog(Window? owner, DskDocument source)
    {
        Owner = owner; Title = "Convert Format — new image"; Width = 800; Height = 590;
        MinWidth = 640; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 10, 0, 0) };
        buttons.Children.Add(convert); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var label = new TextBlock { Text = "Target format (all operations create a separate image):", Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(label, Dock.Top); root.Children.Add(label);
        DockPanel.SetDock(profiles, Dock.Top); root.Children.Add(profiles); root.Children.Add(report); Content = root;
        profiles.ItemsSource = DskCapabilityService.GetAvailableConversions(source).Cast<object>().Concat(new object[] { "HFEv3 (physical MFM)", "HFE (physical MFM)" }).ToArray();
        profiles.SelectionChanged += (_, _) =>
        {
            convert.IsEnabled = false; result = null; physicalResult = null;
            if (profiles.SelectedItem is string format)
            {
                try { physicalResult = MediaConversionService.DskToHfe(source, format.StartsWith("HFEv3", StringComparison.Ordinal)); report.Text = physicalResult.Report; convert.IsEnabled = true; }
                catch (Exception exception) { report.Text = exception.Message; }
                return;
            }
            if (profiles.SelectedItem is not DskConversionProfile profile) return;
            result = DskFileTransferService.Preflight(source, profile);
            report.Text = result.Report;
            convert.IsEnabled = result.CanConvert;
        };
        convert.Click += (_, _) =>
        {
            if (result?.CanConvert == true) { DialogResult = true; return; }
            if (physicalResult == null) return;
            if (MessageBox.Show(this, "Accept the generated timing and metadata losses listed in the preview?", "Confirm DSK → HFE conversion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var save = new SaveFileDialog { Filter = "HFE|*.hfe", FileName = "converted.hfe" };
            if (save.ShowDialog(this) != true) return;
            try { MediaConversionService.WriteVerified(save.FileName, physicalResult.Output, bytes => HfeImage.Parse(bytes), source.FilePath); Close(); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        profiles.SelectedIndex = 0;
    }

    internal static DskConversionPreflight? Show(Window owner, DskDocument source)
    {
        var dialog = new DskConversionDialog(owner, source);
        return dialog.ShowDialog() == true ? dialog.result : null;
    }
}
