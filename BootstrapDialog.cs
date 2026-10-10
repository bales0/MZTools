using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class BootstrapDialog : Window
{
    internal bool Applied { get; private set; }
    internal BootstrapDialog(Window owner, DskDocument document)
    {
        Owner = owner; Title = "IPLPRO Bootstrap Header"; Width = 580; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var metadata = BootstrapService.Inspect(document);
        var panel = new StackPanel { Margin = new Thickness(14) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Edit IPLPRO header metadata or export/replace its complete 4096-byte decoded boot track. Metadata SIZE must match the existing payload; native file type 03 identifies IPLPRO. Replacement previews filesystem overlap and requires confirmation.", TextWrapping = TextWrapping.Wrap });
        TextBox Field(string label, string value) { panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 0) }); var box = new TextBox { Text = value }; panel.Children.Add(box); return box; }
        var name = Field("Name", metadata.Name); var type = Field("File type (hex)", metadata.FileType.ToString("X2")); var size = Field("Size (decimal; existing payload)", metadata.Size.ToString()); var load = Field("LOAD (hex)", metadata.Load.ToString("X4")); var execute = Field("EXEC (hex)", metadata.Execute.ToString("X4"));
        void Show(DskHexEditPreview preview, bool destructive)
        {
            var dialog = new DskPatchPreviewWindow(this, preview.Report, () =>
            {
                bool accept = !destructive && !preview.RequiresFilesystemConfirmation;
                if (!accept) accept = MessageBox.Show(this, "Accept the bootstrap/filesystem impact listed above? This may make the image unbootable.", "Confirm bootstrap change", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                if (!accept) throw new InvalidOperationException("No changes were applied.");
                DskHexEditService.Apply(document, preview, accept);
            }, "Bootstrap Preview", "Apply"); dialog.ShowDialog(); if (dialog.Applied) { Applied = true; Close(); }
        }
        void Button(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(button);
            button.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); } };
        }
        Button("Preview metadata...", () => Show(BootstrapService.PreviewMetadata(document, new(name.Text, byte.Parse(type.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture), ushort.Parse(size.Text, CultureInfo.InvariantCulture), ushort.Parse(load.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture), ushort.Parse(execute.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture))), false));
        Button("Export bootstrap...", () => { var save = new SaveFileDialog { Filter = "Decoded bootstrap track|*.bin" }; if (save.ShowDialog(this) == true) File.WriteAllBytes(save.FileName, BootstrapService.ExportBootstrap(document)); });
        Button("Replace bootstrap...", () => { var open = new OpenFileDialog { Filter = "Decoded bootstrap track|*.bin" }; if (open.ShowDialog(this) == true) Show(BootstrapService.PreviewBootstrap(document, File.ReadAllBytes(open.FileName)), true); });
        Button("Clear bootstrap...", () => Show(BootstrapService.PreviewBootstrap(document, new byte[4096], true), true));
        Button("Cancel", Close);
    }
}
