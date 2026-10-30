using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Platform;

public sealed partial class DesktopDialogs : IMediaDialogs
{
    public async Task<SaveChoice?> ChooseSaveAsync(DocumentSession document)
    {
        if(document is DiskSession disk)
        {
            var diskFile=await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {Title=disk.RequiresRepairSaveAs?"Save repaired copy · choose a new file":"Save disk as · "+disk.Container,SuggestedFileName=(disk.Path is null?"Untitled":Path.GetFileNameWithoutExtension(disk.Path)+(disk.RequiresRepairSaveAs?"-repaired":""))+disk.Extension,ShowOverwritePrompt=false,FileTypeChoices=[new(disk.Container){Patterns=["*"+disk.Extension]}]});
            return diskFile is null?null:new SaveChoice(diskFile.TryGetLocalPath()??throw new NotSupportedException("A local output path is required."),disk.IsHfe?"Hfe":"Dsk",false);
        }
        if (document is not TapeSession tape) { var path = await SaveAsAsync(document); return path is null ? null : new(path); }
        var format = new ComboBox { ItemsSource = FormatChoice.All, SelectedItem = FormatChoice.All.FirstOrDefault(f => f.Key == tape.Format) ?? FormatChoice.All[2] };
        var sidecar = new CheckBox { Content = "Write profile metadata sidecar (.mfi / .m2i / .mti)", IsChecked = tape.GenerateSidecar };
        var trailing = new CheckBox { Content = "Preserve trailing data where supported", IsChecked = tape.PreserveTrailing };
        var options = await FormAsync("Save As options", [Label("Bindable format", format), sidecar, trailing],
            () => new SaveChoice("", ((FormatChoice)format.SelectedItem!).Key, sidecar.IsChecked == true, trailing.IsChecked == true));
        if (options is null) return null;
        var selected = FormatChoice.All.Single(f => f.Key == options.Format);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save document as · " + selected.Label, ShowOverwritePrompt = false,
            SuggestedFileName = (document.Path is null ? "Untitled" : Path.GetFileNameWithoutExtension(document.Path)) + selected.Extension,
            FileTypeChoices = [new(selected.Label) { Patterns = ["*" + selected.Extension] }]
        });
        return file is null ? null : options with { Path = file.TryGetLocalPath() ?? throw new NotSupportedException("A local destination path is required.") };
    }
    public Task<bool> ConfirmLossAsync(string message) => ConfirmPreviewAsync("Output changes", message);
    public async Task<IReadOnlyList<string>> PickImportAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import records or WAV/LEP/L16", AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath() ?? throw new NotSupportedException("A local path is required.")).ToArray();
    }
    public Task<bool> ConfirmPreviewAsync(string title, string report) => ConfirmAsync(title, report, ("Continue", true), ("Cancel", false));
    public async Task<CompressionChoice?> ChooseCompressionAsync()
    {
        var mode = Box(["Keep", "None", "Zx0", "Zx7", "Auto"]); var direction = Box(["Forward", "Backward"]);
        var quick = new CheckBox { Content = "ZX0 quick" }; var embedded = new CheckBox { Content = "ZX7 embedded loader" };
        var skip = new NumericUpDown { Minimum = 0, Maximum = ushort.MaxValue, Value = 0, FormatString = "0" };
        void Update()
        {
            quick.IsEnabled = (string?)mode.SelectedItem == "Zx0";
            embedded.IsEnabled = (string?)mode.SelectedItem == "Zx7";
            direction.IsEnabled = skip.IsEnabled = (string?)mode.SelectedItem is "Zx0" or "Zx7";
        }
        mode.SelectionChanged += (_,_) => Update(); Update();
        return await FormAsync("Compression preview", [Label("Policy (None decodes known compression)", mode), Label("Direction", direction), quick, embedded,
            Label("Uncompressed prefix bytes", skip), new TextBlock { Text = "Available combinations depend on the selected records and target. Core validates every option before Apply.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }],
            () => new CompressionChoice((string)mode.SelectedItem!, direction.IsEnabled ? (string)direction.SelectedItem! : "Forward",
                quick.IsEnabled && quick.IsChecked == true, embedded.IsEnabled && embedded.IsChecked == true, skip.IsEnabled ? (int)skip.Value!.Value : 0));
    }
    public async Task<ExportChoice?> ChooseExportAsync(TapeSession session)
    {
        var choices = FormatChoice.All.Concat(new[] { new FormatChoice("Wav", "WAV audio", ".wav"), new("Lep", "LEP", ".lep"), new("L16", "L16", ".l16"), new("Com", "Native COM", ".com") }).ToArray();
        var format = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };
        var selected = new CheckBox { Content = "Selected records only", IsChecked = true };
        var separate = new CheckBox { Content = "Separate file for each record", IsChecked = false };
        var sidecar = new CheckBox { Content = "Write profile metadata sidecars", IsChecked = true };
        var trailing = new CheckBox { Content = "Preserve trailing data where supported", IsChecked = true };
        var machine = Box(Enum.GetNames<SharpTapeMachine>());
        var cpm = new ComboBox { ItemsSource = CpmTargetProfile.BuiltIns.Select(p => p.Name).ToArray(), SelectedIndex = 0 };
        var native = Box(Enum.GetNames<NativeMachineMode>());
        var compression = new ComboBox { ItemsSource = NativeComCompressionChoice.Choices.Select(c => c.Label).ToArray(), SelectedIndex = 0 };
        var multipart = new CheckBox { Content = "Known multipart native program (explicitly confirmed)" };
        var signalGroup = Label("Tape machine",machine);
        var nativeGroup = new StackPanel { Spacing = 14, Children = { Label("CP/M target",cpm),Label("Machine mode",native),Label("COM compression",compression),multipart } };
        void Update()
        {
            string? key = (format.SelectedItem as FormatChoice)?.Key;
            signalGroup.IsVisible = key is "Wav" or "Lep" or "L16";
            nativeGroup.IsVisible = key == "Com";
            sidecar.IsVisible = key is "Mzf" or "M12" or "Mzt";
            trailing.IsVisible = key is "Mzf" or "M12";
        }
        format.SelectionChanged += (_,_) => Update(); Update();
        var options = await FormAsync("Export options", [Label("Output format", format), selected, separate, sidecar, trailing,
            signalGroup,nativeGroup], () => new ExportChoice("", ((FormatChoice)format.SelectedItem!).Key,
                selected.IsChecked == true, separate.IsChecked == true, sidecar.IsChecked == true, trailing.IsChecked == true,
                (string)machine.SelectedItem!, cpm.SelectedIndex, (string)native.SelectedItem!, compression.SelectedIndex, multipart.IsChecked == true));
        if (options is null) return null;
        var output = choices.Single(c => c.Key == options.Format);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = "Export · " + output.Label, SuggestedFileName = "Export" + output.Extension, ShowOverwritePrompt = false,
            FileTypeChoices = [new(output.Label) { Patterns = ["*" + output.Extension] }] });
        return file is null ? null : options with { Path = file.TryGetLocalPath() ?? throw new NotSupportedException("A local path is required.") };
    }

    private static ComboBox Box(IEnumerable<string> values) => new() { ItemsSource = values.ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static Control Label(string name, Control input)
    {
        Avalonia.Automation.AutomationProperties.SetName(input, name);
        return new StackPanel { Spacing = 4, Children = { new TextBlock { Text = name }, input } };
    }
    private async Task<T?> FormAsync<T>(string title, IEnumerable<Control> fields, Func<T> read) where T : class
    {
        T? result = null;
        var dialog = new Window { Title = title, Width = 560, Height = 620, MinWidth = 420, MinHeight = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var stack = new StackPanel { Spacing = 14 }; foreach (var field in fields) stack.Children.Add(field);
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; stack.Children.Add(error);
        var apply = new Button { Content = "Continue" }; var cancel = new Button { Content = "Cancel", IsCancel = true };
        apply.Click += (_, _) => { try { result = read(); dialog.Close(); } catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException) { error.Text = e.Message; } };
        cancel.Click += (_, _) => dialog.Close();
        var dock = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { apply, cancel } };
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons); dock.Children.Add(new ScrollViewer { Content = stack, Margin = new Thickness(0,0,0,16) });
        dialog.Content = dock; dialog.Opened += (_, _) => cancel.Focus(); await dialog.ShowDialog(owner); return result;
    }
}
