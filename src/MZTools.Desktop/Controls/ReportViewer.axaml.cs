using Avalonia;
using Avalonia.Controls;

namespace MZTools.Desktop.Controls;

public sealed partial class ReportViewer : UserControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ReportViewer, string?>(nameof(Text));
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public ReportViewer() => InitializeComponent();
}
