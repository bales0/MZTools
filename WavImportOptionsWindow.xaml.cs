using System.Windows;

namespace QDTool
{
    internal enum WavImportMode
    {
        Standard,
        Heuristic
    }

    public partial class WavImportOptionsWindow : Window
    {
        internal WavImportMode ImportMode => heuristicImportCheckBox.IsChecked == true
            ? WavImportMode.Heuristic
            : WavImportMode.Standard;

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
