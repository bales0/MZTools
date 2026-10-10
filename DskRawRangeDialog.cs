using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class DskRawRangeDialog : Window
{
    internal bool Applied { get; private set; }
    internal DskRawRangeDialog(Window owner, DskDocument document)
    {
        Owner = owner; Title = "Selected Sectors — Raw Export / Import"; Width = 680; Height = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Select multiple sectors using Ctrl/Shift. Import requires the exact combined byte length. Stored bytes are exported without filesystem inversion.", TextWrapping = TextWrapping.Wrap });
        var order = new ComboBox { ItemsSource = new[] { DskRawOrder.Physical, DskRawOrder.SectorId }, SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 8) }; top.Children.Add(order);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var list = new ListBox { SelectionMode = SelectionMode.Extended, DisplayMemberPath = "SelectionLabel", ItemsSource = DskAnalyzer.Analyze(document).Sectors.ToArray() }; root.Children.Add(list);
        var export = new Button { Content = "Export selected sectors...", Margin = new Thickness(4) }; var import = new Button { Content = "Import selected sectors...", Margin = new Thickness(4) }; buttons.Children.Add(export); buttons.Children.Add(import);
        export.Click += (_, _) =>
        {
            try
            {
                var data = DskRawRangeService.Export(document, list.SelectedItems.Cast<DskSectorLayout>().Select(s => s.Address).ToArray(), (DskRawOrder)order.SelectedItem);
                var save = new SaveFileDialog { Filter = "Raw sectors|*.bin" }; if (save.ShowDialog(this) == true) File.WriteAllBytes(save.FileName, data);
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
        import.Click += (_, _) =>
        {
            try
            {
                var open = new OpenFileDialog { Filter = "Raw sectors|*.bin" }; if (open.ShowDialog(this) != true) return;
                var preview = DskRawRangeService.PreviewImport(document, list.SelectedItems.Cast<DskSectorLayout>().Select(s => s.Address).ToArray(), (DskRawOrder)order.SelectedItem, File.ReadAllBytes(open.FileName));
                var dialog = new DskPatchPreviewWindow(this, preview.Report, () =>
                {
                    bool confirmed = !preview.RequiresFilesystemConfirmation || MessageBox.Show(this, "Accept all filesystem impact in the preview?", "Confirm raw import", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                    if (!confirmed) throw new InvalidOperationException("Import was cancelled.");
                    DskHexEditService.Apply(document, preview, confirmed);
                }, "Multi-sector Import Preview", "Apply import"); dialog.ShowDialog(); if (dialog.Applied) { Applied = true; Close(); }
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
    }
}
