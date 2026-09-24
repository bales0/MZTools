using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QDTool
{
    internal enum DskNewFormat
    {
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

    internal sealed record DskNewOptions(
        DskNewFormat Format,
        int Tracks,
        int Sides,
        int Sectors = 16,
        int SectorSize = 256,
        byte Filler = 0xFF,
        DskDocumentFactory.RawSectorOrder SectorOrder = DskDocumentFactory.RawSectorOrder.Normal,
        IReadOnlyList<int>? SectorIds = null);

    internal sealed class DskNewDialog : Window
    {
        private sealed record FormatChoice(DskNewFormat Format, string Label)
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
                new FormatChoice(DskNewFormat.MzBasic, "MZ-BASIC / FSMZ (63 directory entries)"),
                new FormatChoice(DskNewFormat.IplDisk, "IPLDISK (127 directory entries)"),
                new FormatChoice(DskNewFormat.PersonalCpm, "P-CP/M80 original (320 KiB)"),
                new FormatChoice(DskNewFormat.Sds400, "P-CP/M80 SDS/400"),
                new FormatChoice(DskNewFormat.LecCpmDd, "LEC CP/M DD (9×512 B)"),
                new FormatChoice(DskNewFormat.LecCpmHd, "LEC CP/M HD (18×512 B)"),
                new FormatChoice(DskNewFormat.Mrs, "MRS"),
                new FormatChoice(DskNewFormat.Lemmings, "Sharp Lemmings special geometry"),
                new FormatChoice(DskNewFormat.CustomRaw, "Custom / raw geometry")
            };
            formatBox.SelectedIndex = 0;

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Row("Format:", formatBox));
            panel.Children.Add(Row("Tracks per side:", tracksBox));
            panel.Children.Add(Row("Sides:", sidesBox));
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
            orderBox.SelectionChanged += (_, _) => sectorIdsBox.IsEnabled = orderBox.SelectedIndex == 3;
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
            bool fixedGeometry = choice.Format is DskNewFormat.PersonalCpm or DskNewFormat.Sds400 or DskNewFormat.Lemmings;
            bool custom = choice.Format == DskNewFormat.CustomRaw;
            tracksBox.IsEnabled = !fixedGeometry;
            sidesBox.IsEnabled = !fixedGeometry;
            foreach (FrameworkElement control in customControls) control.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            sectorIdsBox.IsEnabled = custom && orderBox.SelectedIndex == 3;

            switch (choice.Format)
            {
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
                Options = new DskNewOptions(choice.Format, tracks, sides, sectors, sectorSize, filler, order, ids);
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
