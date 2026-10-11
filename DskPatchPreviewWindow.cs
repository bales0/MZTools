using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MZTools;

internal sealed class DskPatchPreviewWindow : Window
{
    internal DskPatchPreviewWindow(Window? owner, string report, Action? apply = null, string title = "DSK Patch Preview", string applyLabel = "Apply patch", ReportPresentation? presentation = null)
    {
        Owner = owner; Title = title; Width = 1120; Height = 720; MinWidth = 720; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) }; Content = panel;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        if (apply != null)
        {
            var applyButton = new Button { Content = applyLabel, MinWidth = 100, Margin = new Thickness(6) }; buttons.Children.Add(applyButton);
            applyButton.Click += (_, _) =>
            {
                try { apply(); Applied = true; Close(); }
                catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
            };
        }
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true, Margin = new Thickness(6) };
        close.Click += (_, _) => Close(); buttons.Children.Add(close);
        var view=new ReportView(); view.Show(presentation??ReportPresentation.FromText(report)); panel.Children.Add(view);
    }
    internal bool Applied { get; private set; }
}
