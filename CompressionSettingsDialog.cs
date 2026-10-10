using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

// Shared complete settings for selected rows in both IPL configuration and the disk editor.
internal sealed class CompressionSettingsDialog : Window
{
    internal MzfCompressionOptions? SelectedOptions { get; private set; }
    internal IReadOnlyList<MzfCompressionResult> PreparedResults { get; private set; } = [];

    internal CompressionSettingsDialog(Window owner, IReadOnlyList<MultiGameIplRow> rows)
    {
        Owner = owner; Title = "IPL compression settings"; Width = 600; Height = 590;
        MinWidth = 440; MinHeight = 430; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) }; Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var apply = new Button { Content = "Apply to selected", IsEnabled = false, IsDefault = true, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4) };
        buttons.Children.Add(apply); buttons.Children.Add(cancel);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = $"Compression for {rows.Count} selected program(s). Changes apply only after Apply.", TextWrapping = TextWrapping.Wrap });
        var settings = new CompressionOptionsControl(); settings.ConfigureTarget(CompressionTarget.IplDsk);
        if (rows.Count > 0) settings.SetOptions(rows[0].CompressionOptions);
        panel.Children.Add(settings);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(status);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 6, Visibility = Visibility.Collapsed }; panel.Children.Add(progress);
        CancellationTokenSource? cancellation = null; int version = 0;
        Closed += (_, _) => { version++; cancellation?.Cancel(); };
        async Task Preview()
        {
            int current = ++version; cancellation?.Cancel(); cancellation?.Dispose(); cancellation = new(); var token = cancellation.Token;
            SelectedOptions = null; PreparedResults = []; apply.IsEnabled = false; progress.Visibility = Visibility.Collapsed;
            if (!settings.TryGetOptions(out var options, out string error)) { status.Text = error; return; }
            progress.Visibility = Visibility.Visible;
            try
            {
                var prepared = new List<MzfCompressionResult>(); long size = 0;
                foreach (var row in rows)
                {
                    await Dispatcher.InvokeAsync(() => { if (current == version) status.Text = $"Preparing {prepared.Count + 1}/{rows.Count}: {row.MenuName}..."; });
                    var result = await MzfCompressionService.CompressAsync(row.Source, options, CompressionTarget.IplDsk, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (current != version) return;
                    prepared.Add(result); size += result.PackedSize;
                }
                if (current != version) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (current != version) return;
                    SelectedOptions = options; PreparedResults = prepared;
                    status.Text = $"Ready: {prepared.Count} program(s), {size:N0} bytes.\n{CompressionOptionsControl.Describe(options)}";
                    progress.Visibility = Visibility.Collapsed;
                    apply.IsEnabled = prepared.Count > 0;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { await Dispatcher.InvokeAsync(() => { if (current == version) status.Text = e.Message; }); }
            finally { await Dispatcher.InvokeAsync(() => { if (current == version) progress.Visibility = Visibility.Collapsed; }); }
        }
        settings.OptionsChanged += async (_, _) => await Preview();
        Loaded += async (_, _) => await Preview();
        apply.Click += (_, _) => { if (SelectedOptions != null && PreparedResults.Count == rows.Count) DialogResult = true; };
    }
}
