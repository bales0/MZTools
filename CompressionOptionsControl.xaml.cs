using System;
using System.Windows;
using System.Windows.Controls;

namespace QDTool
{
    public partial class CompressionOptionsControl : UserControl
    {
        private bool updating;
        private bool initialized;
        private CompressionTarget target = CompressionTarget.MzfTape;

        public CompressionOptionsControl()
        {
            InitializeComponent();
            initialized = true;
            UpdateAvailability();
        }

        internal event EventHandler? OptionsChanged;

        internal void ConfigureTarget(CompressionTarget value)
        {
            target = value;
            bool isIpl = target == CompressionTarget.IplDsk;
            embeddedCheckBox.IsChecked = false;
            embeddedCheckBox.IsEnabled = !isIpl;
            expertExpander.IsExpanded = false;
            expertExpander.IsEnabled = !isIpl;
            skipTextBox.Text = "0";
            targetHintTextBlock.Text = isIpl
                ? "Direct IPL does not load the MZF header. ZX7 embedded loader and partial/skip compression are therefore unavailable."
                : string.Empty;
            targetHintTextBlock.Visibility = isIpl ? Visibility.Visible : Visibility.Collapsed;
            UpdateAvailability();
        }

        internal bool TryGetOptions(out MzfCompressionOptions options, out string error)
        {
            string algorithmName = (algorithmComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "None";
            MzfCompressionAlgorithm algorithm = Enum.Parse<MzfCompressionAlgorithm>(algorithmName);
            if (!int.TryParse(skipTextBox.Text, out int skip) || skip < 0)
            {
                options = new(MzfCompressionAlgorithm.None);
                error = "Skip bytes must be a non-negative integer.";
                return false;
            }

            options = new MzfCompressionOptions(
                algorithm,
                backwardRadioButton.IsChecked == true
                    ? CompressionDirection.Backward
                    : CompressionDirection.Forward,
                quickCheckBox.IsChecked == true,
                embeddedCheckBox.IsChecked == true,
                skip);
            try
            {
                MzfCompressionService.ValidateOptions(options, target, int.MaxValue);
                error = string.Empty;
                return true;
            }
            catch (InvalidOperationException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!initialized || updating)
            {
                return;
            }
            UpdateAvailability();
            OptionsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateAvailability()
        {
            if (algorithmComboBox == null)
            {
                return;
            }

            updating = true;
            string algorithm = (algorithmComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "None";
            bool concrete = algorithm is "Zx0" or "Zx7";
            directionPanel.IsEnabled = concrete;
            quickCheckBox.IsEnabled = algorithm == "Zx0";
            quickCheckBox.Visibility = algorithm == "Zx0" ? Visibility.Visible : Visibility.Collapsed;
            embeddedCheckBox.IsEnabled = algorithm == "Zx7" && target != CompressionTarget.IplDsk;
            embeddedCheckBox.Visibility = algorithm == "Zx7" ? Visibility.Visible : Visibility.Collapsed;
            expertExpander.IsEnabled = concrete && target != CompressionTarget.IplDsk;
            if (algorithm != "Zx0")
            {
                quickCheckBox.IsChecked = false;
            }
            if (algorithm != "Zx7" || target == CompressionTarget.IplDsk)
            {
                embeddedCheckBox.IsChecked = false;
            }
            if (!concrete || target == CompressionTarget.IplDsk)
            {
                skipTextBox.Text = "0";
            }
            updating = false;
        }
    }
}
