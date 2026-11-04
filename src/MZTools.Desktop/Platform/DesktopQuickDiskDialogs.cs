using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Platform;

public sealed partial class DesktopDialogs : INewQuickDiskDialog
{
    async Task<TapeDocumentFormat?> INewQuickDiskDialog.ChooseNewQuickDiskFormatAsync()
    {
        var options = new (string Label, TapeDocumentFormat Format)[]
        {
            ("SHARP legacy logical (.qd)", TapeDocumentFormat.QdSharpLegacy),
            ("HxC physical (.qd)", TapeDocumentFormat.QdHxc),
            ("FlashFloppy physical (.qd)", TapeDocumentFormat.QdFlashFloppy),
            ("MZQ compact (.mzq)", TapeDocumentFormat.Mzq)
        };
        var select = new ComboBox
        {
            ItemsSource = options.Select(option => option.Label).ToArray(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var create = new Button { Content = "Create", IsDefault = true, MinWidth = 76 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 76 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        var content = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "QuickDisk image format" });
        content.Children.Add(select);
        content.Children.Add(new TextBlock
        {
            Text = "Physical QD images use the selected container. The original image is not modified.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Opacity = .72
        });
        content.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "New QuickDisk", Width = 390, SizeToContent = SizeToContent.Height,
            MinHeight = 160, MaxWidth = 480, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        create.Click += (_, _) => dialog.Close((TapeDocumentFormat?)options[select.SelectedIndex].Format);
        cancel.Click += (_, _) => dialog.Close((TapeDocumentFormat?)null);
        return await dialog.ShowDialog<TapeDocumentFormat?>(owner);
    }
}
