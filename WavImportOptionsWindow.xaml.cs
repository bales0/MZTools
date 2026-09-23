using System.Windows;

namespace QDTool
{
    internal enum WavImportMode
    {
        Standard,
        Heuristic
    }

    internal enum AudioReportMode
    {
        Summary,
        Detailed,
        None
    }

    public partial class WavImportOptionsWindow : Window
    {
        internal WavImportMode ImportMode => heuristicImportCheckBox.IsChecked == true
            ? WavImportMode.Heuristic
            : WavImportMode.Standard;

        internal AudioReportMode ReportMode => GetReportMode(reportModeComboBox.SelectedIndex);

        internal static AudioReportMode GetReportMode(int selectedIndex) => selectedIndex switch
        {
            1 => AudioReportMode.Detailed,
            2 => AudioReportMode.None,
            _ => AudioReportMode.Summary
        };

        internal WavImportOptionsWindow(string sourceFormat = "WAV")
        {
            InitializeComponent();
            Title = $"{sourceFormat} import options";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
