using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MZTools;

internal sealed class BatchFileDetailsDialog : Window
{
    internal BatchFileDetailsDialog(Window owner, BatchRow row)
    {
        Owner = owner; Title = "Batch File Details — " + Path.GetFileName(row.Input);
        Width = 1120; Height = 700; MinWidth = 720; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new Thickness(16) }; Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = Path.GetFileName(row.Input), FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = row.Input, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        var badges = new WrapPanel(); header.Children.Add(badges);
        foreach (string text in new[] { "Format: " + Display(row.DetectedFormat), "Status: " + Display(row.Result), "Integrity: " + row.Integrity, "Files / programs: " + (row.Catalog?.FileCount ?? row.Contents.Count) })
            badges.Children.Add(new Border { Background = SystemColors.ControlBrush, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 4), Child = new TextBlock { Text = text } });
        if (row.Reason.Length > 0)
            header.Children.Add(new TextBlock { Text = row.Reason, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        var tabs = new TabControl { Name = "BatchDetailTabs" }; Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        var contents = Table(row.Contents, "BatchContents");
        Column(contents, "#", nameof(BatchContentItem.Number), 45);
        Column(contents, "Name", nameof(BatchContentItem.Name), 200);
        Column(contents, "Size (B)", nameof(BatchContentItem.Size), 100, "{0:N0}");
        Column(contents, "Type (hex)", nameof(BatchContentItem.Type), 85);
        Column(contents, "Load (hex)", nameof(BatchContentItem.Load), 95, "0x{0:X4}");
        Column(contents, "Exec (hex)", nameof(BatchContentItem.Exec), 95, "0x{0:X4}");
        if (row.Contents.Any(c => c.User != null)) Column(contents, "User", nameof(BatchContentItem.User), 60);
        if (row.Contents.Any(c => c.Profile.Length > 0)) Column(contents, "Tape profile", nameof(BatchContentItem.Profile), 115);
        if (row.Contents.Any(c => c.Compression.Length > 0)) Column(contents, "Compression", nameof(BatchContentItem.Compression), 170);
        if (row.Contents.Any(c => c.TrailingBytes > 0)) Column(contents, "Trailing (B)", nameof(BatchContentItem.TrailingBytes), 100, "{0:N0}");
        if (row.Contents.Any(c => c.Attributes.Length > 0)) Column(contents, "Attributes", nameof(BatchContentItem.Attributes), 160);
        tabs.Items.Add(new TabItem { Header = "Contents", Content = row.Contents.Count > 0 ? contents :
            new TextBlock { Text = "No file/program list is available for this source. See Properties and Messages for the inspection result.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) } });

        var fields = new List<BatchDetailField> { new("Operation", Enum.TryParse<BatchOperation>(row.Operation, out var operation) ? BatchMediaPolicy.Label(operation) : row.Operation), new("Filesystem", Display(row.Filesystem)), new("Output", Display(row.Output)) };
        if (row.Catalog is { } catalog)
        {
            fields.AddRange(new BatchDetailField[] {
                new("Capacity", $"{catalog.Capacity:N0} B"), new("Used data", $"{catalog.UsedBytes:N0} B") });
            if (catalog.Geometry.Length > 0)
                fields.AddRange(new BatchDetailField[] {
                    new("Geometry (tracks × sides)", catalog.Geometry), new("Layout / DPB", Display(catalog.Variant)),
                    new("Free space", $"{catalog.FreeBytes:N0} B"), new("Boot / system", Display(catalog.BootType)),
                    new("Boot profile", Display(catalog.BootProfile)), new("Bootable state", catalog.BootableState),
                    new("Decoded sectors", catalog.SectorCount.ToString("N0")), new("CRC error sectors", catalog.CrcErrorSectors.ToString("N0")),
                    new("Weak bits", catalog.WeakBits ? "Present" : "None reported"), new("Bitrate / RPM", Display(catalog.BitrateRpm)) });
            fields.AddRange(new BatchDetailField[] { new("Analyzer errors", catalog.AnalyzerErrors.ToString()), new("Unsafe findings", catalog.AnalyzerUnsafe.ToString()), new("Analyzer warnings", catalog.AnalyzerWarnings.ToString()) });
        }
        fields.AddRange(row.DetailFields);
        fields.Add(new("Source SHA-256", Display(row.Catalog?.Sha256 ?? row.SourceHash)));
        var properties = Table(fields, "BatchProperties");
        Column(properties, "Property", nameof(BatchDetailField.Property), 220);
        Column(properties, "Value", nameof(BatchDetailField.Value), double.NaN);
        tabs.Items.Add(new TabItem { Header = "Properties", Content = properties });

        var messages = new List<BatchDetailField>();
        void Message(string category, string text)
        {
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
                messages.Add(new(category, line));
        }
        Message("Result", row.Reason); Message("Warning", row.Warnings); Message("Error", row.Error);
        var messagesTable = Table(messages, "BatchMessages");
        Column(messagesTable, "Category", nameof(BatchDetailField.Property), 100);
        Column(messagesTable, "Message", nameof(BatchDetailField.Value), double.NaN, wrap: true);
        tabs.Items.Add(new TabItem { Header = $"Messages ({messages.Count})", Content = messagesTable });
        tabs.Items.Add(new TabItem { Header = "Technical details", Content = new TextBox {
            Text = row.DetailedReport, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas") } });
        var checks = Table(row.IntegrityChecks, "BatchIntegrityChecks");
        Column(checks, "File / scope", nameof(BatchIntegrityCheck.Subject), 200);
        Column(checks, "Check", nameof(BatchIntegrityCheck.Check), 180);
        Column(checks, "Result", nameof(BatchIntegrityCheck.Result), 110);
        Column(checks, "Details / limits", nameof(BatchIntegrityCheck.Details), double.NaN, wrap: true);
        tabs.Items.Insert(3, new TabItem { Header = $"Integrity checks ({row.IntegrityChecks.Count})", Content = row.IntegrityChecks.Count > 0 ? checks :
            new TextBlock { Text = "Catalog lists source contents. It does not run a separate integrity test. Select Test integrity to see individual checks and their limits. Audio decoding already checks tape blocks while reading the catalog.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) } });
        if (row.IntegrityChecks.Count > 0) tabs.SelectedIndex = 3;
        if (row.Contents.Count == 0) tabs.SelectedIndex = messages.Any(m => m.Property == "Error") ? 2 : 1;

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 100, Padding = new Thickness(12, 5, 12, 5) };
        DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close); close.Click += (_, _) => Close();
        footer.Children.Add(new TextBlock { Text = "Click a column header to sort. Select cells and press Ctrl+C to copy.", VerticalAlignment = VerticalAlignment.Center });
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static DataGrid Table(IEnumerable items, string name) => new() {
        Name = name, ItemsSource = items, AutoGenerateColumns = false, IsReadOnly = true,
        CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = true,
        SelectionUnit = DataGridSelectionUnit.CellOrRowHeader, SelectionMode = DataGridSelectionMode.Extended,
        ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader, HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, EnableRowVirtualization = true,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private static void Column(DataGrid table, string title, string property, double width, string? format = null, bool wrap = false)
    {
        var column = new DataGridTextColumn { Header = title, Binding = new Binding(property) { StringFormat = format },
            Width = double.IsNaN(width) ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width), MinWidth = 40 };
        if (wrap)
        {
            var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            column.ElementStyle = style;
        }
        table.Columns.Add(column);
    }
}
