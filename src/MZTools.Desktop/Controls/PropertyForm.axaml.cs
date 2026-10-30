using Avalonia;
using Avalonia.Controls;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Controls;

public sealed partial class PropertyForm : UserControl
{
    public static readonly StyledProperty<IEnumerable<PropertyField>?> FieldsProperty =
        AvaloniaProperty.Register<PropertyForm, IEnumerable<PropertyField>?>(nameof(Fields));
    public IEnumerable<PropertyField>? Fields { get => GetValue(FieldsProperty); set => SetValue(FieldsProperty, value); }
    public PropertyForm() => InitializeComponent();
}

public sealed class PropertyField(string label, string text, bool isReadOnly = false, Func<string, string?>? validate = null) : Observable
{
    private string value = text;
    public string Label { get; } = label;
    public bool IsReadOnly { get; } = isReadOnly;
    public string Text
    {
        get => value;
        set { if (!IsReadOnly && Set(ref this.value, value)) { Changed(nameof(Text)); Changed(nameof(Error)); Changed(nameof(HasError)); } }
    }
    public string? Error => validate?.Invoke(Text);
    public bool HasError => Error is not null;
}
