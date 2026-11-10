using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Platform;

public sealed partial class DesktopDialogs(Window owner) : IDocumentDialogs
{
    public bool AudioOnly {get;init;}
    internal Func<FilePickerOpenOptions, Task<IReadOnlyList<string>>>? InputPicker {get;init;}
    internal async Task<IReadOnlyList<string>> OpenInputPickerAsync(FilePickerOpenOptions options)
    {
        using var wait=(owner.DataContext as MZTools.Desktop.ViewModels.ShellViewModel)?.Documents.AwaitUserInput("Choose a file…");
        IReadOnlyList<string> paths;
        if (InputPicker is { } picker) paths = await picker(options);
        else
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(options);
            paths = files.Select(f => f.TryGetLocalPath() ?? throw new NotSupportedException("A local input path is required.")).ToArray();
        }
        FilePickerPolicy.Validate(options, paths);
        return paths;
    }
    private async Task<IStorageFile?> SaveOutputPickerAsync(FilePickerSaveOptions options)
    {
        using var wait=(owner.DataContext as MZTools.Desktop.ViewModels.ShellViewModel)?.Documents.AwaitUserInput("Choose a destination…");
        return await owner.StorageProvider.SaveFilePickerAsync(options);
    }
    public async Task<string?> OpenAsync()
    {
        var options = FilePickerPolicy.Options(AudioOnly ? FilePickerPurpose.OpenAudio : FilePickerPurpose.OpenMedia,
            AudioOnly ? "Open audio / recording" : "Open media");
        var files = await OpenInputPickerAsync(options);
        return files.FirstOrDefault();
    }

    public async Task<string?> SaveAsAsync(DocumentSession document)
    {
        var file = await SaveOutputPickerAsync(new FilePickerSaveOptions
        { Title = "Save document as", SuggestedFileName = document.Path is null ? null : Path.GetFileName(document.Path), ShowOverwritePrompt = false });
        return file is null ? null : file.TryGetLocalPath()
            ?? throw new NotSupportedException("This provider did not return a local path.");
    }

    public Task<bool> ConfirmOverwriteAsync(string path) => ConfirmAsync("Replace destination?",
        "The destination already exists:\n" + path + "\n\nReplace it with this document?",
        ("Replace", true), ("Cancel", false));

    public Task<CloseDecision> ConfirmCloseAsync(DocumentSession document) => document is AudioSession
        ? ConfirmAsync("Unexported analysis / preview", "This analysis contains preview edits or unexported recovery results. The source audio is read-only. Cancel close to export the desired result, or discard the temporary analysis state.", ("Discard analysis draft",CloseDecision.Discard),("Cancel",CloseDecision.Cancel))
        : ConfirmAsync("Unsaved changes",
        "Save changes to " + document.Title + " before closing?",
        ("Save", CloseDecision.Save), ("Discard", CloseDecision.Discard), ("Cancel", CloseDecision.Cancel));

    public Task<bool> ConfirmRecorderCloseAsync()=>ConfirmAsync("Stop recorder?","Stop input, drain and finalize the archive, then close this recorder?",("Stop / finalize and close",true),("Keep recorder open",false));
    public Task<bool> ConfirmDiscardRecordingAsync()=>ConfirmAsync("Discard current run?","Stop and delete only this current run and its manifest. Earlier recordings remain preserved.",("Discard current run",true),("Cancel",false));

    private async Task<T> ConfirmAsync<T>(string title, string message, params (string Caption, T Value)[] choices)
    {
        T result = choices[^1].Value; // Window close is always Cancel.
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
        double availableWidth = screen is null ? 480 : Math.Max(1, screen.WorkingArea.Width / screen.Scaling - 32);
        double messageHeight = screen is null ? 320 : Math.Min(320, Math.Max(40, screen.WorkingArea.Height / screen.Scaling - 160));
        var dialog = new Window
        {
            Title = title, Width = Math.Min(480, availableWidth), SizeToContent = SizeToContent.Height,
            CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button? cancel = null;
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice.Caption, Margin = new Thickness(4), IsCancel = choice.Equals(choices[^1]) };
            button.Click += (_, _) => { result = choice.Value; dialog.Close(); };
            buttons.Children.Add(button); cancel = button;
        }
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        body.Children.Add(new ScrollViewer { MaxHeight = messageHeight,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap } });
        body.Children.Add(buttons);
        dialog.Content = body;
        dialog.Opened += (_, _) => cancel?.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }
}
