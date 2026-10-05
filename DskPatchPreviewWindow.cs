using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MZTools;

internal sealed class DskPatchPreviewWindow : Window
{
    internal DskPatchPreviewWindow(Window? owner, string report, Action? apply = null)
    {
        Owner = owner; Title = "DSK Patch Preview"; Width = 850; Height = 650; MinWidth = 600; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) }; Content = panel;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        if (apply != null)
        {
            var applyButton = new Button { Content = "Apply patch", MinWidth = 100, Margin = new Thickness(6) }; buttons.Children.Add(applyButton);
            applyButton.Click += (_, _) =>
            {
                try { apply(); Applied = true; Close(); }
                catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
            };
        }
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true, Margin = new Thickness(6) };
        close.Click += (_, _) => Close(); buttons.Children.Add(close);
        panel.Children.Add(new TextBox { Text = report, IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
    internal bool Applied { get; private set; }
}
