using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MZTools;

internal static class SeparateExportNaming
{
    internal const string DefaultMask = "{index}_{name}{ext}";
    internal static string[] BuildPaths(string folder, string mask, IReadOnlyList<TapeRecord> records, string extension)
    {
        if (string.IsNullOrWhiteSpace(mask)) throw new ArgumentException("Enter a filename mask.");
        string remaining = mask.Replace("{name}", "").Replace("{index}", "").Replace("{ext}", "");
        if (remaining.Contains('{') || remaining.Contains('}')) throw new ArgumentException("Supported placeholders: {name}, {index}, {ext}.");
        string root = Path.GetFullPath(folder);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return records.Select((record, index) =>
        {
            string name = SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname);
            name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (name.Length == 0) name = "program";
            if (Reserved(name)) name = "_" + name;
            string filename = mask.Replace("{name}", name).Replace("{index}", (index + 1).ToString("D3")).Replace("{ext}", extension);
            if (!mask.Contains("{ext}")) filename += extension;
            if (filename.Length > 255 || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || filename.EndsWith(' ') || filename.EndsWith('.') || Reserved(filename))
                throw new ArgumentException("The mask produces an invalid filename: " + filename);
            if (!filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Place {ext} at the end of the mask.");
            string path = Path.Combine(root, filename);
            if (!paths.Add(path)) throw new ArgumentException("The mask produces duplicate filenames. Add {index} to distinguish programs.");
            return path;
        }).ToArray();
    }

    private static bool Reserved(string name)
    {
        string stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]);
    }
}

internal sealed class SeparateExportDialog : Window
{
    internal IReadOnlyList<string> OutputPaths { get; private set; } = [];
    internal sealed record PreviewRow(int Number, string Program, string Filename);
    internal SeparateExportDialog(Window owner, IReadOnlyList<TapeRecord> records, string extension)
    {
        Owner = owner; Title = "Export separate files"; Width = 780; Height = 500; MinWidth = 580; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = $"Export {records.Count} programs as separate {extension.TrimStart('.').ToUpperInvariant()} files", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        var folderRow = new DockPanel(); header.Children.Add(folderRow);
        var browse = new Button { Content = "Browse…", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right); folderRow.Children.Add(browse);
        folderRow.Children.Add(new TextBlock { Text = "Output folder", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        var folder = new TextBox { Name = "ExportFolder" }; folderRow.Children.Add(folder);
        var maskRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; header.Children.Add(maskRow);
        maskRow.Children.Add(new TextBlock { Text = "Filename mask", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        var mask = new TextBox { Name = "ExportMask", Text = SeparateExportNaming.DefaultMask }; maskRow.Children.Add(mask);
        header.Children.Add(new TextBlock { Text = "{name} = program name from header    {index} = 001, 002, …    {ext} = selected extension", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) }; footer.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(buttons);
        var export = new Button { Content = "Export", IsDefault = true, MinWidth = 100, Padding = new Thickness(8, 4, 8, 4) };
        buttons.Children.Add(export); buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) });
        var table = new DataGrid { Name = "ExportNamesPreview", IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
        table.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding("Number"), Width = 45 });
        table.Columns.Add(new DataGridTextColumn { Header = "Program", Binding = new Binding("Program"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridTextColumn { Header = "Output filename", Binding = new Binding("Filename"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        root.Children.Add(table);
        void Preview()
        {
            OutputPaths = []; export.IsEnabled = false;
            try
            {
                string[] paths = SeparateExportNaming.BuildPaths(string.IsNullOrWhiteSpace(folder.Text) ? Path.GetTempPath() : folder.Text, mask.Text, records, extension);
                table.ItemsSource = paths.Select((path, index) => new PreviewRow(index + 1, SharpMzEncoding.ConvertMzfNameToASCIIString(records[index].Header.MzfFname), Path.GetFileName(path))).ToArray();
                if (!Directory.Exists(folder.Text)) { status.Text = "Choose an existing output folder."; return; }
                if (paths.Any(Directory.Exists)) { status.Text = "An output filename is already used by a directory."; return; }
                OutputPaths = paths; export.IsEnabled = true;
                status.Text = $"{paths.Length} files. Existing files and sidecars will require overwrite confirmation.";
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            { table.ItemsSource = null; status.Text = e.Message; }
        }
        folder.TextChanged += (_, _) => Preview(); mask.TextChanged += (_, _) => Preview();
        browse.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "Choose export folder", Multiselect = false }; if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName; };
        export.Click += (_, _) => { Preview(); if (export.IsEnabled) DialogResult = true; };
        Preview();
    }
}
