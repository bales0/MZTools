using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Platform;

public sealed partial class DesktopDialogs(Window owner) : IDocumentDialogs
{
    public async Task<string?> OpenAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = "Open media · editor availability depends on the module", AllowMultiple = false });
        return files.Count == 0 ? null : files[0].TryGetLocalPath()
            ?? throw new NotSupportedException("This provider did not return a local path.");
    }

    public async Task<string?> SaveAsAsync(DocumentSession document)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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
        var dialog = new Window
        {
            Title = title, Width = 560, Height = 520, CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, MinHeight = 280, MinWidth = 400
        };
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button? cancel = null;
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice.Caption, Margin = new Thickness(4), IsCancel = choice.Equals(choices[^1]) };
            button.Click += (_, _) => { result = choice.Value; dialog.Close(); };
            buttons.Children.Add(button); cancel = button;
        }
        var body = new DockPanel { Margin = new Thickness(24) }; DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons); body.Children.Add(new ScrollViewer { Margin = new Thickness(0,0,0,20),
            Content = new TextBox { Text = message, IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, BorderThickness = new Thickness(0) } });
        dialog.Content = body;
        dialog.Opened += (_, _) => cancel?.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }
}
