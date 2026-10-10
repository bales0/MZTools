using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class CrossDiskTransferDialog : Window
{
    internal bool Applied { get; private set; }
    internal IReadOnlyList<string> CopiedFileKeys { get; private set; } = Array.Empty<string>();
    internal CrossDiskTransferDialog(Window owner, DskDocument target, DskDocument? droppedSource = null, string[]? droppedKeys = null)
    {
        Owner = owner; Title = "Disk workspace — copy to current disk"; Width = 950; Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var sources = new Dictionary<string, DskDocument>();
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        var open = new Button { Content = "Open source disks...", Margin = new Thickness(4) };
        var source = new ComboBox { MinWidth = 340, Margin = new Thickness(4) };
        var policy = new ComboBox { ItemsSource = Enum.GetValues<DiskCollisionPolicy>(), SelectedItem = DiskCollisionPolicy.Cancel, Margin = new Thickness(4) };
        var preview = new Button { Content = "Preview copy...", Margin = new Thickness(4), IsEnabled = false, ToolTip = "Open a source disk and select files to copy." };
        ToolTipService.SetShowOnDisabled(preview, true);
        actions.Children.Add(open); actions.Children.Add(source); actions.Children.Add(new TextBlock { Text = "Collision:", VerticalAlignment = VerticalAlignment.Center }); actions.Children.Add(policy); actions.Children.Add(preview);
        var info = new TextBlock { Text = $"Target (current disk): {target.FilePath ?? "unsaved image"}\n{target.FileSystem.DisplayName}; free {target.FileSystem.FreeBytes} B\nSelect source files; preview lists metadata changes. Apply changes the target in memory. Save / Save As remains separate.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 8) };
        DockPanel.SetDock(info, Dock.Top); root.Children.Add(info);
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Extended };
        foreach (string property in new[] { "User", "Name", "Extension", "Size", "LoadAddress", "ExecuteAddress", "ReadOnly", "System", "Archived", "Locked" })
            grid.Columns.Add(new DataGridTextColumn { Header = property, Binding = new System.Windows.Data.Binding(property) });
        root.Children.Add(grid);
        grid.SelectionChanged += (_, _) => preview.IsEnabled = source.SelectedItem != null && grid.SelectedItems.Count > 0;
        source.SelectionChanged += (_, _) => grid.ItemsSource = source.SelectedItem is string path ? sources[path].FileSystem.ReadDirectory() : null;
        open.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "DSK images|*.dsk", Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                foreach (string path in picker.FileNames)
                {
                    var disk = DskDocument.Open(path);
                    if (!CrossDiskTransferService.Supports(disk)) throw new InvalidOperationException(path + ": requires a consistent FSMZ, CP/M or MRS image.");
                    sources[path] = disk;
                }
                source.ItemsSource = sources.Keys.ToArray(); source.SelectedItem = picker.FileNames[0];
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
        preview.Click += (_, _) =>
        {
            if (source.SelectedItem is not string path || grid.SelectedItems.Count == 0) { MessageBox.Show(this, "Open a source and select files.", Title); return; }
            try
            {
                var result = CrossDiskTransferService.Preview(sources[path], target, grid.SelectedItems.Cast<DskFileEntry>().Select(e => new DiskCopyRequest(e.Key)).ToArray(), (DiskCollisionPolicy)policy.SelectedItem);
                var dialog = new DskPatchPreviewWindow(this, result.Operation.Report, () =>
                {
                    result.Operation.Apply(target);
                }, "Cross-disk copy preview", "Apply to current disk");
                dialog.ShowDialog(); if (dialog.Applied) { CopiedFileKeys = result.TargetKeys ?? Array.Empty<string>(); Applied = true; Close(); }
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, Title); }
        };
        if (droppedSource != null)
        {
            const string label = "Dragged selection (source snapshot)";
            sources.Add(label, droppedSource);
            open.Visibility = Visibility.Collapsed;
            source.ItemsSource = new[] { label }; source.SelectedItem = label;
            source.IsEnabled = false;
            var selected = droppedSource.FileSystem.ReadDirectory().Where(e => droppedKeys!.Contains(e.Key)).ToArray();
            if (selected.Length != droppedKeys!.Length) throw new InvalidOperationException("Dragged source selection is no longer valid.");
            grid.ItemsSource = selected;
            foreach (var entry in selected) grid.SelectedItems.Add(entry);
            info.Text += "\nDrag and drop always copies; the source selection is never removed.";
        }
    }
}
