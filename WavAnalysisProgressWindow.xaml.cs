using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.ComponentModel;

namespace QDTool
{
    public partial class WavAnalysisProgressWindow : Window
    {
        private readonly string filePath;
        private readonly CancellationTokenSource cancellation = new();
        private bool analysisStarted;
        private bool analysisFinished;

        internal WavHeuristicAnalysisResult? AnalysisResult { get; private set; }
        internal Exception? AnalysisError { get; private set; }

        internal WavAnalysisProgressWindow(string filePath)
        {
            this.filePath = filePath;
            InitializeComponent();
            stageText.Text = $"Preparing {Path.GetFileName(filePath)}…";
        }

        private async void Window_ContentRendered(object sender, EventArgs e)
        {
            if (analysisStarted)
            {
                return;
            }
            analysisStarted = true;
            var progress = new Progress<WavAnalysisProgress>(UpdateProgress);
            try
            {
                AnalysisResult = await Task.Run(() =>
                    WavHeuristicAnalyzer.AnalyzeFile(
                        filePath,
                        progress,
                        cancellation.Token));
                analysisFinished = true;
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                analysisFinished = true;
                DialogResult = false;
            }
            catch (Exception exception)
            {
                AnalysisError = exception;
                analysisFinished = true;
                DialogResult = false;
            }
        }

        private void UpdateProgress(WavAnalysisProgress progress)
        {
            double percentage = Math.Clamp(progress.Fraction * 100, 0, 100);
            stageText.Text = progress.Stage;
            progressBar.Value = percentage;
            progressText.Text =
                $"{percentage:F1} %  ·  {progress.ProcessedFrames:N0} / {progress.TotalFrames:N0} frames" +
                $"  ·  {progress.CandidateCount:N0} candidates";
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            cancelButton.IsEnabled = false;
            stageText.Text = "Cancelling…";
            cancellation.Cancel();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (!analysisFinished)
            {
                e.Cancel = true;
                Cancel_Click(this, new RoutedEventArgs());
            }
        }
    }
}
