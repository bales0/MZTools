using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MZTools;

internal sealed class DskHexEditorWindow : Window
{
    private readonly DskHexEditorControl editor;
    internal DskHexEditorWindow(Window? owner, DskDocument document, DskHexEditSession session)
    {
        Owner = owner; Title = "Hex Editor — " + session.Title;
        Width = Math.Min(1320, SystemParameters.WorkArea.Width - 40); Height = 740; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        editor = new(document, session); Content = editor;
        editor.Completed += (_, _) => Close();
    }
    internal bool Applied => editor.Applied;
    internal void UnlockEditing() => editor.UnlockEditing();
    internal void SetEditedHex(string text) => editor.SetEditedHex(text);
    internal void Revert() => editor.Revert();
    internal void PreviewChanges() => editor.PreviewChanges();
    internal void ApplyPreview(bool acceptFilesystemImpact = false) => editor.ApplyPreview(acceptFilesystemImpact);
}

internal sealed class DskHexEditorControl : UserControl
{
    private readonly DskDocument document;
    private readonly DskHexEditSession session;
    private readonly TextBox edited = HexBox();
    private readonly TextBox diff = HexBox();
    private readonly TextBox editedAscii = CharacterBox();
    private readonly TextBox editedMz = CharacterBox();
    private readonly ScrollViewer originalScroll = BufferScroll();
    private readonly ScrollViewer editedScroll = BufferScroll();
    private bool synchronizingScroll;
    private bool editingUnlocked;
    private readonly Button previewButton = new() { Content = "Preview changes", IsEnabled = false, Margin = new Thickness(6) };
    private readonly Button apply = new() { Content = "Apply", IsEnabled = false, Margin = new Thickness(6), MinWidth = 90 };
    private DskHexEditPreview? preview;
    internal bool Applied { get; private set; }
    internal event EventHandler? Completed;
    internal bool HasPendingChanges => edited.Text != DskHexEditService.FormatHex(session.OriginalBuffer) && !Applied;

    internal DskHexEditorControl(DskDocument document, DskHexEditSession session)
    {
        this.document = document; this.session = session;
        edited.IsReadOnlyCaretVisible = true;
        var root = new Grid { Margin = new Thickness(12) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new TextBlock { Text = $"{session.Title}\nRole: {session.Role}. Fixed-size overwrite editor: type 0–9/A–F; paste complete hexadecimal bytes. Delete cannot remove bytes." +
            (session.IsSensitive ? "\nWARNING: Boot/System/Directory/FAT/AllocationMap edits can prevent booting or damage the filesystem." : ""), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var buffers = new Grid(); buffers.ColumnDefinitions.Add(new()); buffers.ColumnDefinitions.Add(new());
        var original = HexBox(); original.Text = DskHexEditService.FormatHex(session.OriginalBuffer);
        edited.Text = original.Text;
        buffers.Children.Add(BufferPanel("Original buffer", original, CharacterBox(), CharacterBox(), originalScroll, session.OriginalBuffer));
        var editPanel = BufferPanel("New buffer — initially locked", edited, editedAscii, editedMz, editedScroll, session.OriginalBuffer);
        Grid.SetColumn(editPanel, 1); buffers.Children.Add(editPanel);
        originalScroll.ScrollChanged += SynchronizeScroll;
        editedScroll.ScrollChanged += SynchronizeScroll;
        Grid.SetRow(buffers, 1); root.Children.Add(buffers);
        diff.Text = "Unlock editing, edit bytes, then preview the complete change before applying.";
        var diffPanel = Panel("Byte diff and filesystem impact", diff); Grid.SetRow(diffPanel, 2); root.Children.Add(diffPanel);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var unlock = new Button { Content = "Unlock editing", Margin = new Thickness(6) };
        var revert = new Button { Content = "Revert", Margin = new Thickness(6) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(6), MinWidth = 90 };
        controls.Children.Add(unlock); controls.Children.Add(revert); controls.Children.Add(previewButton); controls.Children.Add(apply); controls.Children.Add(cancel);
        Grid.SetRow(controls, 3); root.Children.Add(controls);
        edited.TextChanged += (_, _) =>
        {
            preview = null; apply.IsEnabled = false; diff.Text = "Preview is stale. Preview changes again before applying.";
            try { UpdateCharacters(editedAscii, editedMz, DskHexEditService.ParseHex(edited.Text, session.OriginalBuffer.Length)); }
            catch (FormatException) { editedAscii.Text = editedMz.Text = "Invalid / incomplete HEX"; }
            catch (ArgumentException) { editedAscii.Text = editedMz.Text = "Invalid / incomplete HEX"; }
        };
        // Keep the native TextBox read-only even after unlocking. All byte changes
        // go through fixed-position operations; cut/drop/IME cannot reshape it.
        edited.PreviewTextInput += (_, e) => { e.Handled = true; OverwriteDigits(e.Text); };
        edited.PreviewKeyDown += (_, e) =>
        {
            if (!editingUnlocked) return;
            if (e.Key is Key.Delete or Key.Space or Key.Enter or Key.Insert) e.Handled = true;
            else if (e.Key == Key.Back || ((e.Key is Key.Left or Key.Right) && Keyboard.Modifiers == ModifierKeys.None))
            {
                edited.Select(FixedHexInput.Move(edited.Text, edited.SelectionStart, e.Key == Key.Right ? 1 : -1), 0);
                e.Handled = true;
            }
        };
        edited.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste,
            (_, e) =>
            {
                e.Handled = true;
                try { if (Clipboard.ContainsText()) PasteHex(Clipboard.GetText()); }
                catch (System.Runtime.InteropServices.ExternalException) { diff.Text = "Clipboard is temporarily unavailable."; }
            }, (_, e) => { e.CanExecute = editingUnlocked; e.Handled = true; }));
        unlock.Click += (_, _) =>
        {
            if (session.IsSensitive && MessageBox.Show(Window.GetWindow(this),
                "This buffer includes boot/system or filesystem metadata. Editing it can damage files or prevent booting. Unlock editing?",
                "Unlock sensitive buffer", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            UnlockEditing(); unlock.IsEnabled = false;
        };
        revert.Click += (_, _) => Revert();
        previewButton.Click += (_, _) =>
        {
            try { PreviewChanges(); }
            catch (Exception exception) { diff.Text = exception.Message; apply.IsEnabled = false; }
        };
        apply.Click += (_, _) =>
        {
            if (preview == null) return;
            bool accept = false;
            if (preview.RequiresFilesystemConfirmation)
            {
                accept = MessageBox.Show(Window.GetWindow(this), "Filesystem detection or validation changed. Apply these bytes anyway?\n\n" + preview.Report,
                    "Confirm filesystem impact", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                if (!accept) return;
            }
            try { ApplyPreview(accept); Completed?.Invoke(this, EventArgs.Empty); }
            catch (Exception exception) { diff.Text = exception.Message; apply.IsEnabled = false; }
        };
        cancel.Click += (_, _) => Cancel();
    }

    internal void UnlockEditing() { editingUnlocked = true; previewButton.IsEnabled = true; }
    internal bool OverwriteDigits(string digits)
    {
        if (!editingUnlocked || !FixedHexInput.TryOverwrite(edited.Text, edited.SelectionStart, digits, out string output, out int next)) return false;
        edited.Text = output; edited.Select(next, 0); return true;
    }
    internal bool PasteHex(string text)
    {
        if (!editingUnlocked) return false;
        if (!FixedHexInput.TryPaste(edited.Text, edited.SelectionStart, text, out string output, out int next))
        { diff.Text = "Paste rejected: enter complete HEX bytes that fit in the remaining buffer. No bytes were changed."; return false; }
        edited.Text = output; edited.Select(next, 0); return true;
    }
    internal void Cancel() { Revert(); Completed?.Invoke(this, EventArgs.Empty); }
    internal void SetEditedHex(string text) => edited.Text = text;
    internal void Revert() { edited.Text = DskHexEditService.FormatHex(session.OriginalBuffer); preview = null; apply.IsEnabled = false; }
    internal void PreviewChanges()
    {
        if (!editingUnlocked) throw new InvalidOperationException("Unlock editing first.");
        preview = null; apply.IsEnabled = false;
        preview = DskHexEditService.Preview(session, DskHexEditService.ParseHex(edited.Text, session.OriginalBuffer.Length));
        diff.Text = preview.Report; apply.IsEnabled = preview.Changes.Count != 0;
    }
    internal void ApplyPreview(bool acceptFilesystemImpact = false)
    {
        if (preview == null || !apply.IsEnabled) throw new InvalidOperationException("Preview the current edits before Apply.");
        DskHexEditService.Apply(document, preview, acceptFilesystemImpact); Applied = true;
    }
    private static TextBox HexBox() => new() { IsReadOnly = true, AcceptsReturn = true, AcceptsTab = false,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 5, 8, 8) };
    private static TextBox CharacterBox() => new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"),
        Padding = new Thickness(2), Margin = new Thickness(0, 5, 8, 8), TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static ScrollViewer BufferScroll() => new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 8, 0) };

    private void SynchronizeScroll(object sender, ScrollChangedEventArgs e)
    {
        if (synchronizingScroll || !ReferenceEquals(sender, e.OriginalSource)) return;
        var other = ReferenceEquals(sender, originalScroll) ? editedScroll : originalScroll;
        var source = (ScrollViewer)sender;
        synchronizingScroll = true;
        try { other.ScrollToVerticalOffset(source.VerticalOffset); other.ScrollToHorizontalOffset(source.HorizontalOffset); }
        finally { synchronizingScroll = false; }
    }

    internal static string FormatCharacters(byte[] bytes, bool mz)
    {
        var result = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0 && i % 16 == 0) result.AppendLine();
            byte value = mz ? SharpMzEncoding.FromSHASCII(bytes[i]) : bytes[i];
            result.Append(value is >= 32 and <= 126 ? (char)value : '.');
        }
        return result.ToString();
    }
    private static void UpdateCharacters(TextBox ascii, TextBox mz, byte[] bytes)
    { ascii.Text = FormatCharacters(bytes, false); mz.Text = FormatCharacters(bytes, true); }

    private static Grid BufferPanel(string label, TextBox hex, TextBox ascii, TextBox mz, ScrollViewer scroll, byte[] bytes)
    {
        hex.VerticalScrollBarVisibility = hex.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        hex.Padding = new Thickness(2); hex.TextWrapping = TextWrapping.NoWrap;
        var columns = new Grid();
        for (int i = 0; i < 3; i++) columns.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        columns.RowDefinitions.Add(new() { Height = GridLength.Auto }); columns.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var boxes = new[] { hex, ascii, mz }; var labels = new[] { "HEX", "ASCII", "MZ (SHASCII)" };
        for (int i = 0; i < boxes.Length; i++)
        {
            var heading = new TextBlock { Text = labels[i] }; Grid.SetColumn(heading, i); columns.Children.Add(heading);
            Grid.SetColumn(boxes[i], i); Grid.SetRow(boxes[i], 1); columns.Children.Add(boxes[i]);
        }
        UpdateCharacters(ascii, mz, bytes); scroll.Content = columns;
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new());
        panel.Children.Add(new TextBlock { Text = label }); Grid.SetRow(scroll, 1); panel.Children.Add(scroll); return panel;
    }
    private static Grid Panel(string label, TextBox text)
    {
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new());
        panel.Children.Add(new TextBlock { Text = label }); Grid.SetRow(text, 1); panel.Children.Add(text); return panel;
    }
}
