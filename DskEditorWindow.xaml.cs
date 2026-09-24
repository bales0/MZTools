using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace QDTool
{
    public partial class DskEditorWindow : Window
    {
        private readonly DskDocument document;

        internal DskEditorWindow(DskDocument document)
        {
            this.document = document;
            InitializeComponent();
            ConfigureColumns();
            RefreshView();
        }

        private IReadOnlyList<DskFileEntry> SelectedEntries => directoryGrid.SelectedItems.Cast<DskFileEntry>().ToList();

        private void ConfigureColumns()
        {
            void Add(string header, string property) => directoryGrid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = DataGridLength.Auto });
            switch (document.FileSystem.Type)
            {
                case DskFileSystemType.Fsmz:
                    Add("Name", nameof(DskFileEntry.Name)); Add("Type", nameof(DskFileEntry.FileType)); Add("Size", nameof(DskFileEntry.Size));
                    Add("Load", nameof(DskFileEntry.LoadAddress)); Add("Exec", nameof(DskFileEntry.ExecuteAddress)); Add("Start block", nameof(DskFileEntry.StartBlock)); Add("Locked", nameof(DskFileEntry.Locked));
                    break;
                case DskFileSystemType.Cpm:
                    Add("User", nameof(DskFileEntry.User)); Add("Name", nameof(DskFileEntry.Name)); Add("Ext", nameof(DskFileEntry.Extension)); Add("Size", nameof(DskFileEntry.Size));
                    Add("RO", nameof(DskFileEntry.ReadOnly)); Add("SYS", nameof(DskFileEntry.System)); Add("ARC", nameof(DskFileEntry.Archived)); Add("Extents", nameof(DskFileEntry.Extents)); Add("Blocks", nameof(DskFileEntry.Blocks));
                    break;
                case DskFileSystemType.Mrs:
                    Add("Name", nameof(DskFileEntry.Name)); Add("Ext", nameof(DskFileEntry.Extension)); Add("Blocks", nameof(DskFileEntry.Blocks)); Add("Approx. size", nameof(DskFileEntry.Size));
                    Add("Load", nameof(DskFileEntry.LoadAddress)); Add("Exec", nameof(DskFileEntry.ExecuteAddress)); Add("File ID", nameof(DskFileEntry.StartBlock)); Add("Note", nameof(DskFileEntry.Notes));
                    break;
                default:
                    Add("Track", nameof(DskFileEntry.Name)); Add("Sector", nameof(DskFileEntry.Extension)); Add("Size", nameof(DskFileEntry.Size)); Add("Physical index", nameof(DskFileEntry.Blocks)); Add("Status", nameof(DskFileEntry.Notes));
                    break;
            }
        }

        private void RefreshView()
        {
            directoryGrid.ItemsSource = document.FileSystem.ReadDirectory();
            DskImage image = document.Image;
            string sizes = string.Join(", ", image.Tracks.Where(track => track != null).SelectMany(track => track!.Sectors).Select(sector => sector.Data.Length).Distinct().Order());
            infoText.Text = $"Container: Extended CPC DSK | Creator: {image.Creator} | Tracks: {image.TrackCount} | Sides: {image.SideCount} | Physical tracks: {image.Tracks.Count} | Image: {image.Serialize().Length:N0} B\n" +
                $"Filesystem: {document.FileSystem.DisplayName} | Sector sizes: {sizes} B | Used: {document.FileSystem.UsedBytes:N0} B | Free: {document.FileSystem.FreeBytes:N0} B";
            warningText.Text = document.FileSystem.Warnings.Count == 0 ? string.Empty : string.Join("  ", document.FileSystem.Warnings);
            Title = $"MZTools DSK editor - {Path.GetFileName(document.FilePath) ?? "new image"}{(document.IsModified ? " *" : string.Empty)}";
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureWritable()) return;
            if (document.FileSystem is RawDskFileSystem raw)
            {
                if (SelectedEntries.Count != 1) { ShowError("Select exactly one sector to replace."); return; }
                var sectorDialog = new OpenFileDialog { Filter = "Raw sector data|*.bin;*.*|All files|*.*" };
                if (sectorDialog.ShowDialog(this) != true) return;
                try { raw.ReplaceSector(SelectedEntries[0], File.ReadAllBytes(sectorDialog.FileName)); document.MarkModified(); RefreshView(); }
                catch (Exception exception) { ShowError(exception.Message); }
                return;
            }
            var dialog = new OpenFileDialog { Filter = "MZF and binary files|*.mzf;*.bin;*.*|All files|*.*", Multiselect = true };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                foreach (string path in dialog.FileNames)
                {
                    ImportPath(path);
                    document.MarkModified();
                }
                RefreshView();
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<DskFileEntry> entries = SelectedEntries;
            if (entries.Count == 0) return;
            var dialog = new SaveFileDialog { Filter = "Raw file|*.bin|MZF file|*.mzf", FileName = SuggestedName(entries[0]) };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                string folder = Path.GetDirectoryName(dialog.FileName)!;
                for (int index = 0; index < entries.Count; index++)
                {
                    DskFileEntry entry = entries[index];
                    string path = entries.Count == 1 ? dialog.FileName : Path.Combine(folder, SuggestedName(entry));
                    byte[] data = document.FileSystem.Extract(entry);
                    if (Path.GetExtension(dialog.FileName).Equals(".mzf", StringComparison.OrdinalIgnoreCase) && document.FileSystem.Type is DskFileSystemType.Fsmz or DskFileSystemType.Mrs)
                        File.WriteAllBytes(Path.ChangeExtension(path, ".mzf"), DskMzfConverter.Serialize(entry, data));
                    else File.WriteAllBytes(path, data);
                }
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureWritable()) return;
            IReadOnlyList<DskFileEntry> entries = SelectedEntries;
            if (entries.Count == 0 || MessageBox.Show(this, $"Delete {entries.Count} selected item(s)?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { foreach (DskFileEntry entry in entries) { document.FileSystem.Delete(entry, force: true); document.MarkModified(); } RefreshView(); }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureWritable()) return;
            if (SelectedEntries.Count != 1) return;
            DskFileEntry entry = SelectedEntries[0];
            string? value = TextPrompt.Show(this, "Rename", "New file name:", SuggestedName(entry));
            if (value == null) return;
            try { document.FileSystem.Rename(entry, value); document.MarkModified(); RefreshView(); }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Hex_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedEntries.Count != 1) return;
            try
            {
                DskFileEntry entry = SelectedEntries[0]; byte[] logical = document.FileSystem.Extract(entry);
                var browser = new HexBrowser { Owner = this }; browser.ShowRawData(SuggestedName(entry), logical); browser.Show();
            }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (document.FilePath == null) { SaveAs_Click(sender, e); return; }
            try { document.Save(); RefreshView(); } catch (Exception exception) { ShowError(exception.Message); }
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Filter = "Extended CPC DSK|*.dsk", FileName = Path.GetFileName(document.FilePath) ?? "disk.dsk" };
            if (dialog.ShowDialog(this) != true) return;
            try { document.Save(dialog.FileName); RefreshView(); } catch (Exception exception) { ShowError(exception.Message); }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!EnsureWritable()) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (document.FileSystem is RawDskFileSystem) { ShowError("Use Add with one selected sector to replace raw sector data."); return; }
            try { foreach (string path in files) { ImportPath(path); document.MarkModified(); } RefreshView(); }
            catch (Exception exception) { ShowError(exception.Message); }
        }

        private void ImportPath(string path)
        {
            if (Path.GetExtension(path).Equals(".mzf", StringComparison.OrdinalIgnoreCase))
            {
                TapeRecord record = new MZTFileReader().ReadStandaloneMzf(path);
                document.FileSystem.Insert(SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname), record.Body.MzfBody,
                    record.Header.MzfFtype, record.Header.MzfStart, record.Header.MzfExec);
            }
            else
            {
                document.FileSystem.Insert(Path.GetFileName(path), File.ReadAllBytes(path));
            }
        }

        private bool EnsureWritable()
        {
            if (!document.IsReadOnly) return true;
            ShowError("This disk is read-only because structural inconsistencies were detected.");
            return false;
        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!document.IsModified) return;
            MessageBoxResult result = MessageBox.Show(this, "Save changes to this DSK image?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Cancel) e.Cancel = true;
            else if (result == MessageBoxResult.Yes) { Save_Click(this, new RoutedEventArgs()); e.Cancel = document.IsModified; }
        }

        private static string SuggestedName(DskFileEntry entry) => string.IsNullOrEmpty(entry.Extension) ? entry.Name : $"{entry.Name}.{entry.Extension}";
        private void ShowError(string message) => MessageBox.Show(this, message, "DSK editor", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    internal static class DskMzfConverter
    {
        internal static byte[] Serialize(DskFileEntry entry, byte[] data)
        {
            var header = new byte[128]; header[0] = entry.FileType == 0 ? (byte)1 : entry.FileType;
            byte[] name = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(entry.Name);
            name.AsSpan(0, Math.Min(16, name.Length)).CopyTo(header.AsSpan(1, 16)); header[17] = 0x0D;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18, 2), checked((ushort)Math.Min(data.Length, ushort.MaxValue)));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), entry.LoadAddress);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), entry.ExecuteAddress);
            return header.Concat(data.Take(ushort.MaxValue)).ToArray();
        }
    }

    internal static class TextPrompt
    {
        internal static string? Show(Window owner, string title, string label, string initial)
        {
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 5, 0, 10), MinWidth = 280 };
            var window = new Window { Owner = owner, Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var panel = new StackPanel { Margin = new Thickness(12) }; panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(box);
            var button = new Button { Content = "OK", IsDefault = true, MinWidth = 75, HorizontalAlignment = HorizontalAlignment.Right }; button.Click += (_, _) => window.DialogResult = true; panel.Children.Add(button); window.Content = panel;
            return window.ShowDialog() == true ? box.Text : null;
        }
    }

    internal static class ChoicePrompt
    {
        internal static string? Show(Window owner, string title, string label, IReadOnlyList<string> choices)
        {
            var selector = new ComboBox { ItemsSource = choices, SelectedIndex = 0, MinWidth = 310, Margin = new Thickness(0, 5, 0, 10) };
            var window = new Window { Owner = owner, Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var panel = new StackPanel { Margin = new Thickness(12) }; panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(selector);
            var button = new Button { Content = "Create", IsDefault = true, MinWidth = 75, HorizontalAlignment = HorizontalAlignment.Right }; button.Click += (_, _) => window.DialogResult = true; panel.Children.Add(button); window.Content = panel;
            return window.ShowDialog() == true ? selector.SelectedItem as string : null;
        }
    }
}
