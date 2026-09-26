using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace QDTool
{
    internal enum DskNewFormat
    {
        IplSingle,
        IplMulti,
        MzBasic,
        IplDisk,
        PersonalCpm,
        Sds400,
        LecCpmDd,
        LecCpmHd,
        Mrs,
        Lemmings,
        CustomRaw
    }

    internal enum DskBootMode
    {
        FormatDefault,
        Empty,
        ImportFromDsk
    }

    internal sealed record DskNewOptions(
        DskNewFormat Format,
        int Tracks,
        int Sides,
        int Sectors = 16,
        int SectorSize = 256,
        byte Filler = 0xFF,
        DskDocumentFactory.RawSectorOrder SectorOrder = DskDocumentFactory.RawSectorOrder.Normal,
        IReadOnlyList<int>? SectorIds = null,
        DskBootMode BootMode = DskBootMode.FormatDefault,
        string? BootSourcePath = null);

    internal sealed class DskNewDialog : Window
    {
        private sealed record FormatChoice(DskNewFormat Format, string Label)
        {
            public override string ToString() => Label;
        }

        private sealed record BootChoice(DskBootMode Mode, string Label)
        {
            public override string ToString() => Label;
        }

        private readonly ComboBox formatBox = new() { MinWidth = 390 };
        private readonly TextBox tracksBox = new() { Text = "40", MinWidth = 100 };
        private readonly ComboBox sidesBox = new() { ItemsSource = new[] { "1", "2" }, SelectedIndex = 1, MinWidth = 100 };
        private readonly TextBox sectorsBox = new() { Text = "16", MinWidth = 100 };
        private readonly ComboBox sectorSizeBox = new() { ItemsSource = new[] { "128", "256", "512", "1024" }, SelectedIndex = 1, MinWidth = 100 };
        private readonly TextBox fillerBox = new() { Text = "FF", MinWidth = 100 };
        private readonly ComboBox orderBox = new() { ItemsSource = new[] { "Normal", "LEC interleave 2", "LEC HD interleave 3", "Custom sector IDs" }, SelectedIndex = 0, MinWidth = 180 };
        private readonly TextBox sectorIdsBox = new() { Text = "1,2,3,4,5,6,7,8,9", MinWidth = 250 };
        private readonly TextBlock capacityText = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly ComboBox bootBox = new() { MinWidth = 390 };
        private readonly TextBox bootPathBox = new() { MinWidth = 320, IsReadOnly = true };
        private readonly Button bootBrowseButton = new() { Content = "Browse...", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        private readonly FrameworkElement bootRow;
        private readonly FrameworkElement bootPathRow;
        private readonly FrameworkElement[] customControls;

        private DskNewDialog(Window owner)
        {
            Owner = owner;
            Title = "New DSK";
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            formatBox.ItemsSource = new[]
            {
                new FormatChoice(DskNewFormat.IplSingle, "MZ IPL DSK — single program (320 KiB)"),
                new FormatChoice(DskNewFormat.IplMulti, "MZTools IPL DSK — multiple programs (320 KiB)"),
                new FormatChoice(DskNewFormat.MzBasic, "MZ-BASIC / FSMZ — 63 directory entries (standard 320 KiB)"),
                new FormatChoice(DskNewFormat.IplDisk, "IPLDISK filesystem — 127 directory entries (standard 320 KiB)"),
                new FormatChoice(DskNewFormat.PersonalCpm, "P-CP/M80 original (320 KiB)"),
                new FormatChoice(DskNewFormat.Sds400, "P-CP/M80 SDS/400 (400 KiB)"),
                new FormatChoice(DskNewFormat.LecCpmDd, "LEC CP/M DD — 9×512 B (standard 720 KiB)"),
                new FormatChoice(DskNewFormat.LecCpmHd, "LEC CP/M HD — 18×512 B (standard 1.44 MiB)"),
                new FormatChoice(DskNewFormat.Mrs, "MRS (standard 720 KiB)"),
                new FormatChoice(DskNewFormat.Lemmings, "Sharp Lemmings special geometry (720 KiB)"),
                new FormatChoice(DskNewFormat.CustomRaw, "Custom / raw geometry (calculated capacity)")
            };
            // Keep the existing general-purpose filesystem preset as the default;
            // the two IPL image builders are additional DSK formats, not a new default.
            formatBox.SelectedIndex = 2;

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Row("Format:", formatBox));
            panel.Children.Add(Row("Tracks per side:", tracksBox));
            panel.Children.Add(Row("Sides:", sidesBox));
            panel.Children.Add(Row("Capacity:", capacityText));
            bootRow = Row("Boot track:", bootBox);
            var bootPathPanel = new StackPanel { Orientation = Orientation.Horizontal };
            bootPathPanel.Children.Add(bootPathBox);
            bootPathPanel.Children.Add(bootBrowseButton);
            bootPathRow = Row("Boot source DSK:", bootPathPanel);
            panel.Children.Add(bootRow);
            panel.Children.Add(bootPathRow);
            FrameworkElement sectorsRow = Row("Sectors per track:", sectorsBox);
            FrameworkElement sizeRow = Row("Sector size:", sectorSizeBox);
            FrameworkElement fillerRow = Row("Filler (hex):", fillerBox);
            FrameworkElement orderRow = Row("Sector order:", orderBox);
            FrameworkElement idsRow = Row("Custom IDs:", sectorIdsBox);
            customControls = [sectorsRow, sizeRow, fillerRow, orderRow, idsRow];
            foreach (FrameworkElement row in customControls) panel.Children.Add(row);

            var create = new Button { Content = "Create", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 10, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 10, 0, 0) };
            create.Click += Create_Click;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(create);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;

            formatBox.SelectionChanged += (_, _) => UpdateFields();
            bootBox.SelectionChanged += (_, _) => UpdateBootPathVisibility();
            bootBrowseButton.Click += BootBrowse_Click;
            tracksBox.TextChanged += (_, _) => UpdateCapacity();
            sidesBox.SelectionChanged += (_, _) => UpdateCapacity();
            sectorsBox.TextChanged += (_, _) => UpdateCapacity();
            sectorSizeBox.SelectionChanged += (_, _) => UpdateCapacity();
            orderBox.SelectionChanged += (_, _) =>
            {
                sectorIdsBox.IsEnabled = orderBox.SelectedIndex == 3;
                UpdateCapacity();
            };
            UpdateFields();
        }

        internal DskNewOptions? Options { get; private set; }

        internal static DskNewOptions? Show(Window owner)
        {
            var dialog = new DskNewDialog(owner);
            return dialog.ShowDialog() == true ? dialog.Options : null;
        }

        private static FrameworkElement Row(string label, FrameworkElement control)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
            return grid;
        }

        private void UpdateFields()
        {
            if (formatBox.SelectedItem is not FormatChoice choice) return;
            bool fixedGeometry = choice.Format is DskNewFormat.IplSingle or DskNewFormat.IplMulti or
                DskNewFormat.PersonalCpm or DskNewFormat.Sds400 or DskNewFormat.Lemmings;
            bool custom = choice.Format == DskNewFormat.CustomRaw;
            bool cpm = IsCpmFormat(choice.Format);
            tracksBox.IsEnabled = !fixedGeometry;
            sidesBox.IsEnabled = !fixedGeometry;
            foreach (FrameworkElement control in customControls) control.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            sectorIdsBox.IsEnabled = custom && orderBox.SelectedIndex == 3;
            bootRow.Visibility = cpm ? Visibility.Visible : Visibility.Collapsed;
            if (cpm)
            {
                bool generatedDefault = choice.Format is DskNewFormat.PersonalCpm or DskNewFormat.Sds400;
                bootBox.ItemsSource = generatedDefault
                    ? new[]
                    {
                        new BootChoice(DskBootMode.FormatDefault, "Generated IPLPRO header (default)"),
                        new BootChoice(DskBootMode.Empty, "Empty boot track"),
                        new BootChoice(DskBootMode.ImportFromDsk, "Import bootable CP/M system area from DSK...")
                    }
                    : new[]
                    {
                        new BootChoice(DskBootMode.FormatDefault, "Empty boot track (default)"),
                        new BootChoice(DskBootMode.ImportFromDsk, "Import bootable CP/M system area from DSK...")
                    };
                bootBox.SelectedIndex = 0;
            }
            UpdateBootPathVisibility();

            switch (choice.Format)
            {
                case DskNewFormat.IplSingle:
                case DskNewFormat.IplMulti:
                case DskNewFormat.MzBasic:
                case DskNewFormat.IplDisk:
                case DskNewFormat.PersonalCpm:
                case DskNewFormat.Sds400:
                    tracksBox.Text = "40";
                    sidesBox.SelectedIndex = 1;
                    break;
                default:
                    tracksBox.Text = "80";
                    sidesBox.SelectedIndex = 1;
                    break;
            }
            UpdateCapacity();
        }

        private static bool IsCpmFormat(DskNewFormat format) => format is
            DskNewFormat.PersonalCpm or DskNewFormat.Sds400 or
            DskNewFormat.LecCpmDd or DskNewFormat.LecCpmHd;

        private void UpdateBootPathVisibility()
        {
            bool visible = bootRow.Visibility == Visibility.Visible &&
                bootBox.SelectedItem is BootChoice { Mode: DskBootMode.ImportFromDsk };
            bootPathRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BootBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select a bootable CP/M system DSK",
                Filter = "DSK disk images (*.dsk)|*.dsk|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) == true)
            {
                bootPathBox.Text = dialog.FileName;
            }
        }

        private void UpdateCapacity()
        {
            if (formatBox.SelectedItem is not FormatChoice choice ||
                !int.TryParse(tracksBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tracks) ||
                sidesBox.SelectedItem is not string sidesValue ||
                !int.TryParse(sidesValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sides))
            {
                capacityText.Text = "—";
                return;
            }

            long bytes;
            string suffix = string.Empty;
            switch (choice.Format)
            {
                case DskNewFormat.IplSingle:
                case DskNewFormat.IplMulti:
                case DskNewFormat.MzBasic:
                case DskNewFormat.IplDisk:
                    bytes = (long)tracks * sides * 16 * 256;
                    break;
                case DskNewFormat.PersonalCpm:
                    bytes = 320L * 1024;
                    break;
                case DskNewFormat.Sds400:
                    bytes = 400L * 1024;
                    break;
                case DskNewFormat.LecCpmDd:
                case DskNewFormat.Mrs:
                case DskNewFormat.Lemmings:
                    bytes = (long)tracks * sides * 9 * 512;
                    break;
                case DskNewFormat.LecCpmHd:
                    bytes = (long)tracks * sides * 18 * 512;
                    break;
                case DskNewFormat.CustomRaw:
                    if (!int.TryParse(sectorsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sectors) ||
                        sectorSizeBox.SelectedItem is not string sectorSizeValue ||
                        !int.TryParse(sectorSizeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sectorSize))
                    {
                        capacityText.Text = "—";
                        return;
                    }
                    bytes = (long)tracks * sides * sectors * sectorSize;
                    suffix = " (raw sector data)";
                    break;
                default:
                    capacityText.Text = "—";
                    return;
            }

            capacityText.Text = bytes % (1024 * 1024) == 0
                ? $"{bytes / (1024 * 1024)} MiB / {bytes:N0} B{suffix}"
                : $"{bytes / 1024.0:0.##} KiB / {bytes:N0} B{suffix}";
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FormatChoice choice = (FormatChoice)formatBox.SelectedItem;
                int tracks = ParseRange(tracksBox.Text, "Tracks per side", 1, DskImage.MaximumAbsoluteTracks);
                int sides = sidesBox.SelectedIndex + 1;
                if (checked(tracks * sides) > DskImage.MaximumAbsoluteTracks)
                    throw new InvalidOperationException($"Tracks × sides cannot exceed {DskImage.MaximumAbsoluteTracks}.");

                int sectors = 16;
                int sectorSize = 256;
                byte filler = 0xFF;
                DskDocumentFactory.RawSectorOrder order = DskDocumentFactory.RawSectorOrder.Normal;
                IReadOnlyList<int>? ids = null;
                if (choice.Format == DskNewFormat.CustomRaw)
                {
                    sectors = ParseRange(sectorsBox.Text, "Sectors per track", 1, DskImage.MaximumSectorsPerTrack);
                    sectorSize = int.Parse((string)sectorSizeBox.SelectedItem, CultureInfo.InvariantCulture);
                    string fillerText = fillerBox.Text.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? fillerBox.Text.Trim()[2..] : fillerBox.Text.Trim();
                    filler = byte.Parse(fillerText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    order = (DskDocumentFactory.RawSectorOrder)orderBox.SelectedIndex;
                    if (order == DskDocumentFactory.RawSectorOrder.Custom)
                    {
                        int[] parsed = sectorIdsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(value => ParseRange(value, "Sector ID", 0, 255)).ToArray();
                        if (parsed.Length != sectors) throw new InvalidOperationException($"Enter exactly {sectors} custom sector IDs.");
                        if (parsed.Distinct().Count() != parsed.Length) throw new InvalidOperationException("Custom sector IDs must be unique.");
                        ids = parsed;
                    }
                }
                DskBootMode bootMode = bootBox.SelectedItem is BootChoice bootChoice
                    ? bootChoice.Mode
                    : DskBootMode.FormatDefault;
                string? bootSourcePath = null;
                if (bootMode == DskBootMode.ImportFromDsk)
                {
                    bootSourcePath = bootPathBox.Text.Trim();
                    if (bootSourcePath.Length == 0 || !File.Exists(bootSourcePath))
                    {
                        throw new InvalidOperationException("Select an existing DSK image containing the boot track.");
                    }
                }
                Options = new DskNewOptions(choice.Format, tracks, sides, sectors, sectorSize, filler, order, ids, bootMode, bootSourcePath);
                DialogResult = true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "New DSK", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static int ParseRange(string value, string label, int minimum, int maximum)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) || result < minimum || result > maximum)
                throw new InvalidOperationException($"{label} must be in the range {minimum}..{maximum}.");
            return result;
        }
    }
}
