using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MZTools;

internal static class Program
{
    private static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) {
            var child=VisualTreeHelper.GetChild(parent,i); if(child is T found) yield return found;
            foreach(var nested in Children<T>(child)) yield return nested;
        }
    }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private static void CheckBatchOperations(Window parent)
    {
        var batch = new BatchProcessDialog(parent); batch.Show(); batch.UpdateLayout();
        try
        {
            var operations = Children<ComboBox>(batch).Single(c => c.Name == "BatchOperation");
            var choices = operations.Items.Cast<ComboBoxItem>().ToArray();
            Check(!choices.Any(c => c.Tag.ToString() == "ApplyPatch" || c.Content.ToString()!.Contains("patch", StringComparison.OrdinalIgnoreCase)), "Batch does not offer Apply Patch");
            Check(Equals(((ComboBoxItem)operations.SelectedItem).Tag, BatchOperation.TestIntegrity), "Batch keeps its integrity-test default");
            var label = Children<TextBlock>(batch).Single(t => t.Text == "Boot source / profile");
            var row = (Grid)label.Parent;
            var source = row.Children.OfType<TextBox>().Single(); var browse = row.Children.OfType<Button>().Single();
            foreach (var choice in choices)
            {
                operations.SelectedItem = choice;
                bool boot = Equals(choice.Tag, BatchOperation.InstallBootSystem);
                Check(source.IsEnabled == boot && browse.IsEnabled == boot, "Boot source/profile is enabled only for boot installation");
            }
            Check(!Children<TextBlock>(batch).Any(t => t.Text.Contains("Boot source / patch")), "No stale patch input label");
            Console.WriteLine("PASS: Batch operations exclude Apply Patch; boot source/profile and all remaining operation selections retain correct availability.");
        }
        finally { batch.Close(); }
    }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint thread, EnumWindowCallback callback, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr command, IntPtr parameter);
    private static void AnswerTruncate(bool accept)
    {
        IntPtr confirmation = IntPtr.Zero;
        EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            var title = new System.Text.StringBuilder(256); GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "Confirm destructive truncate") confirmation = window;
            return true;
        }, IntPtr.Zero);
        Check(confirmation != IntPtr.Zero, "Separate native destructive confirmation is visible");
        SendMessage(confirmation, 0x111, new IntPtr(accept ? 6 : 7), IntPtr.Zero); // WM_COMMAND: Yes / No.
    }
    private static void CheckDskOpen()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mztools-dsk-open-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var main = new MainWindow(); main.Show();
        var editor = (DskEditorControl)main.FindName("dskEditorControl");
        object? Invoke(string method, params object?[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, args);
        DskDocument Current() => (DskDocument)typeof(DskEditorControl).GetField("document", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!;
        void RepairCurrent() => typeof(DskEditorControl).GetMethod("RepairContainer_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, new object[] { editor, new RoutedEventArgs() });
        Exception? failure = null;
        void DriveRepair(Action<DskRepairDialog> action) => main.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = main.OwnedWindows.OfType<DskRepairDialog>().Single();
            try { action(dialog); }
            catch (Exception e) { failure = e; dialog.Close(); }
        }));
        try
        {
            var containerMenu = (MenuItem)editor.FindName("containerRepairMenu");
            var filesystemMenu = (MenuItem)editor.FindName("filesystemRepairMenu");
            Check(!containerMenu.IsEnabled && Equals(containerMenu.Header, "Repair Container…") && ToolTipService.GetShowOnDisabled(containerMenu), "Container repair initially disabled with explanatory tooltip");
            RepairCurrent(); Check(main.OwnedWindows.Count == 0, "Empty editor repair never opens a picker");
            var tools = ((Button)main.FindName("toolsButton")).ContextMenu;
            Check(main.FindName("repairDskFileMenu") == null && !tools.Items.OfType<MenuItem>().Any(m => m.Header.ToString()!.Contains("Repair DSK")), "Main Tools has no duplicate container repair");
            Check(typeof(MainWindow).GetMethod("RepairDskFile_Click", BindingFlags.NonPublic | BindingFlags.Instance) == null, "Removed standalone repair handler");
            byte[] healthy = DskImage.CreateUniform(3, 1, 2, 512, 1, 0x4E, 0xE5, "open smoke").Serialize();
            string path = Path.Combine(folder, "source.dsk");
            File.WriteAllBytes(path, healthy);
            var opened = (DskDocument)Invoke("OpenDskWithRepair", path)!;
            Check(opened.Serialize().SequenceEqual(healthy) && !opened.IsModified && main.OwnedWindows.Count == 0, "Healthy Open has no repair dialog");
            editor.LoadDocument(opened);
            Check(!containerMenu.IsEnabled && containerMenu.ToolTip.ToString()!.Contains("No container repair is needed"), "Healthy disk repair is disabled with precise status");
            RepairCurrent(); Check(main.OwnedWindows.Count == 0, "Healthy disk never opens an empty repair dialog");
            var ambiguous = healthy.ToArray(); ambiguous[0x34] = 1; ambiguous[256 + 0x10] = 7;
            foreach (var (sample, expectedStatus) in new[] { (healthy, "No container repair is needed"),
                (new byte[128], "Unsupported or unparseable"), (ambiguous, "Cannot safely repair") })
            {
                var status = new DskRepairDialog(main, sample); status.Show(); status.UpdateLayout();
                Check(Children<Button>(status).Count() == 1 && Children<Button>(status).All(b => b.IsEnabled && Equals(b.Content, "Close")), "Status dialog has only an enabled Close action");
                Check(Children<TextBlock>(status).Any(t => t.Text.Contains(expectedStatus)), "Healthy/unsupported/ambiguous states are distinguished");
                Check(!Children<TextBlock>(status).Any(t => t.Text.Contains("NotRepairable")), "No misleading NotRepairable repair row");
                status.Close();
            }
            var fsmz = DskDocumentFactory.CreateFsmz(tracks: 4); editor.LoadDocument(fsmz);
            Check(((Button)editor.FindName("applyPatchButton")).IsEnabled, "Individual disk editor retains Apply DSK patch");
            Check(filesystemMenu.IsEnabled && filesystemMenu.ToolTip.ToString()!.Contains("filesystem metadata") && !containerMenu.IsEnabled, "Filesystem Guided Repair remains independent");
            byte[] filesystemBefore = fsmz.Serialize();
            main.Dispatcher.BeginInvoke(new Action(() =>
            {
                var preview = main.OwnedWindows.OfType<DskPatchPreviewWindow>().Single();
                Check(preview.Title == "Safe Filesystem Repair Preview", "Filesystem repair has its own preview");
                Children<Button>(preview).Single(b => Equals(b.Content, "Apply repair")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            typeof(DskEditorControl).GetMethod("RepairFilesystem_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, new object[] { editor, new RoutedEventArgs() });
            Check(fsmz.Serialize().SequenceEqual(filesystemBefore) && !fsmz.RequiresRepairSaveAs, "Clean filesystem repair remains byte-preserving and independent of container repair state");
            var hfe = HfeImage.Parse(HfeImage.FromDsk(fsmz).Serialize(HfeVersion.V1));
            editor.LoadDocument(DskDocument.OpenHfe(hfe));
            Check(!containerMenu.IsEnabled && containerMenu.ToolTip.ToString()!.Contains("HFE"), "HFE container repair stays unavailable");
            RepairCurrent(); Check(main.OwnedWindows.Count == 0, "HFE repair handler cannot open DSK repair");
            editor.LoadNewSingleIpl(); Check(!containerMenu.IsEnabled, "Empty IPL draft disables container repair");
            File.WriteAllBytes(path, healthy.Concat(new byte[] { 1, 2, 3 }).ToArray());
            var trailing = (DskDocument)Invoke("OpenDskWithRepair", path)!;
            Check(trailing.Serialize().SequenceEqual(File.ReadAllBytes(path)) && main.OwnedWindows.Count == 0, "Trailing data opens without prompt or truncation");
            byte[] damaged = healthy.ToArray(); damaged[0x34] = 1; File.WriteAllBytes(path, damaged);
            var active = DskDocument.Open(healthy); active.MarkModified();
            editor.LoadDocument(active); editor.Visibility = Visibility.Visible;
            DriveRepair(dialog =>
            {
                Check(Children<Button>(dialog).Any(b => Equals(b.Tag, "DSK_TSIZE")) && Children<Button>(dialog).All(b => b.IsEnabled), "Open shows actionable repairs");
                Children<Button>(dialog).Single(b => Equals(b.Content, "Cancel")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Invoke("AddFile", path, false, false, null, null, null, false, 0, 0);
            Check(failure == null && ReferenceEquals(Current(), active) && active.IsModified && editor.Visibility == Visibility.Visible, "Cancel preserves visible unsaved active disk");
            Check(File.ReadAllBytes(path).SequenceEqual(damaged), "Cancel preserves source bytes");
            damaged = healthy.ToArray(); damaged[0x30] = 2; File.WriteAllBytes(path, damaged);
            DriveRepair(dialog => Children<Button>(dialog).Single(b => Equals(b.Content, "Open without repair")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            var declined = (DskDocument)Invoke("OpenDskWithRepair", path)!;
            Check(failure == null && !declined.IsModified && declined.Serialize().SequenceEqual(damaged), "Decline opens parseable original unchanged");
            // Replace only a clean current disk after an explicit preview/Apply.
            editor.LoadDocument(DskDocument.Open(healthy));
            damaged = healthy.ToArray(); damaged[0x34] = 1; File.WriteAllBytes(path, damaged);
            DriveRepair(dialog =>
            {
                dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var preview = dialog.OwnedWindows.OfType<DskPatchPreviewWindow>().Single();
                    Children<Button>(preview).Single(b => Equals(b.Content, "Apply repair")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                Children<Button>(dialog).Single(b => Equals(b.Tag, "DSK_TSIZE")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Invoke("AddFile", path, false, false, null, null, null, false, 0, 0);
            Check(failure == null && Current().RequiresRepairSaveAs && Current().IsModified, "Confirmed repair reaches normal disk editor as unsaved document");
            Check(Current().Serialize().SequenceEqual(healthy) && File.ReadAllBytes(path).SequenceEqual(damaged), "Apply changes only repaired memory copy");
            Check(((TextBlock)editor.FindName("warningText")).Text.Contains("Save As"), "Editor communicates required Save As");
            Current().Save(Path.Combine(folder, "source_repaired.dsk"));
            Check(File.ReadAllBytes(path).SequenceEqual(damaged), "Save As preserves original source");
            Check(Directory.GetFiles(folder).Length == 2, "No unexpected working files remain");
            damaged = healthy.ToArray(); damaged[0x30] = 2; File.WriteAllBytes(path, damaged);
            var manual = DskDocument.Open(damaged, path); editor.LoadDocument(manual);
            Check(containerMenu.IsEnabled, "Current document exposes an actionable header repair");
            DriveRepair(dialog =>
            {
                dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var preview = dialog.OwnedWindows.OfType<DskPatchPreviewWindow>().Single();
                    Children<Button>(preview).Single(b => Equals(b.Content, "Apply repair")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }));
                Children<Button>(dialog).Single(b => Equals(b.Tag, "DSK_TRACK_COUNT")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            RepairCurrent();
            Check(failure == null && ReferenceEquals(Current(), manual) && manual.RequiresRepairSaveAs && !containerMenu.IsEnabled, "Editor repair uses the current document and refreshes availability after Apply");
            Check(manual.Serialize().SequenceEqual(healthy) && File.ReadAllBytes(path).SequenceEqual(damaged), "Manual header repair preserves original file");
            manual.Save(Path.Combine(folder, "editor_repaired.dsk"));
            byte[] withTrailing = healthy.Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray(); File.WriteAllBytes(path, withTrailing);
            var optional = DskDocument.Open(withTrailing, path); editor.LoadDocument(optional);
            Check(containerMenu.IsEnabled && containerMenu.ToolTip.ToString()!.Contains("destructive"), "Trailing removal is an optional manual action");
            int confirmations = 0;
            foreach (bool accept in new[] { false, true })
            {
                DriveRepair(dialog =>
                {
                    Check(Children<TextBlock>(dialog).Any(t => t.Text.Contains("Optional trailing-data removal")), "Truncate status explains its optional nature");
                    dialog.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var preview = dialog.OwnedWindows.OfType<DskPatchPreviewWindow>().Single();
                        preview.Dispatcher.BeginInvoke(new Action(() => { AnswerTruncate(accept); confirmations++; }));
                        Children<Button>(preview).Single(b => Equals(b.Content, "Apply repair")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (!accept)
                        {
                            Check(!preview.Applied && dialog.Result == null && optional.Serialize().SequenceEqual(withTrailing), "Declining truncate keeps preview open and data unchanged");
                            Children<Button>(preview).Single(b => Equals(b.Content, "Close")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            dialog.Close();
                        }
                    }));
                    Children<Button>(dialog).Single(b => Equals(b.Tag, "DSK_TRAILING_DATA")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                RepairCurrent();
                Check(failure == null && File.ReadAllBytes(path).SequenceEqual(withTrailing), "Truncate decisions never overwrite original");
                Check(Current().RequiresRepairSaveAs == accept && containerMenu.IsEnabled != accept, "Truncate decision refreshes current-document availability without stale state");
            }
            Check(confirmations == 2 && Current().RequiresRepairSaveAs && Current().Serialize().SequenceEqual(healthy), "Only confirmed truncate applies in memory and requires Save As");
            Current().Save(Path.Combine(folder, "trailing_repaired.dsk"));
            Console.WriteLine("PASS: actual DSK Open/AddFile, healthy/trailing no-prompt, Cancel preserves active dirty disk, decline, preview/Apply, editor state and Save As source protection.");
            Console.WriteLine("PASS: Tools cleanup, current-document menu availability, healthy/unsupported statuses, HFE/IPL guards, independent filesystem repair, manual header repair and native truncate No/Yes confirmations.");
        }
        finally
        {
            editor.LoadDocument(DskDocumentFactory.CreateRaw(3, 1, 2, 512, 1, 0x4E, 0xE5, "cleanup"));
            main.Close(); Directory.Delete(folder, true);
        }
    }
    private static async Task CheckNativeCom(Window parent)
    {
        var control = new CompressionOptionsControl();
        control.ConfigureTarget(CompressionTarget.NativeCom);
        var combo = (ComboBox)control.FindName("algorithmComboBox");
        var group = (GroupBox)control.FindName("compressionGroupBox");
        ComboBoxItem Item(string tag) => combo.Items.Cast<ComboBoxItem>().Single(i => Equals(i.Tag, tag));
        Check(control.KeepSource && Equals(group.Header, "Output compression"), "COM default and output heading");
        Check(Equals(Item("None").Content, "Uncompressed (decode recognized ZX0/ZX7)"), "COM None label");
        Check(Equals(Item("Auto").Content, "Auto (smallest valid COM)"), "COM Auto label");
        var info = new MzfCompressionInfo(MzfCompressionAlgorithm.Zx7, CompressionDirection.Backward, true, 0, 0, 0, 0, 0, 0, 0, 0);
        int events = 0;
        control.OptionsChanged += (_, _) => events++;
        foreach (string tag in new[] { "KeepSource", "None", "Auto", "Zx0", "Zx7" })
        {
            combo.SelectedItem = Item(tag);
            var selected = combo.SelectedItem;
            int before = events;
            control.SetSourceCompression(info);
            Check(Equals(Item("KeepSource").Content, "Keep original compression"), "Detected Keep label");
            control.SetSourceCompression(null);
            Check(Equals(Item("KeepSource").Content, "Keep source encoding (no recompression)"), "Unknown Keep label");
            Check(ReferenceEquals(selected, combo.SelectedItem) && events == before, "Relabel preserves selection without conversion events");
            Check(control.TryGetOptions(out var options, out _), "COM options remain valid");
            Check(options.Algorithm.ToString() == (tag == "KeepSource" ? "None" : tag), "COM tags retain policy mapping");
            Check(((StackPanel)control.FindName("directionPanel")).IsEnabled == (tag is "Zx0" or "Zx7"), "Direction availability");
            Check(((CheckBox)control.FindName("quickCheckBox")).IsEnabled == (tag == "Zx0"), "Quick availability");
            Check(((CheckBox)control.FindName("embeddedCheckBox")).IsEnabled == (tag == "Zx7"), "Embedded availability");
            Check(!((Expander)control.FindName("expertExpander")).IsEnabled, "COM partial compression stays disabled");
        }
        foreach (var target in new[] { CompressionTarget.MzfTape, CompressionTarget.MztTape, CompressionTarget.QuickDisk, CompressionTarget.IplDsk })
        {
            control.ConfigureTarget(target);
            control.SetSourceCompression(info);
            Check(Equals(group.Header, "Compression") && combo.Items.Count == 4, "Other targets retain heading and choices");
            Check(Equals(Item("None").Content, "None") && Equals(Item("Auto").Content, "Auto (smallest safe)"), "Other targets retain labels");
            Check(Item("None").ToolTip == null && Item("Auto").ToolTip == null, "COM explanations do not leak to other targets");
        }
        // Incompatible CP/M placement must not hide the recognized source encoding.
        byte[] bytes = new byte[128 + 256];
        bytes[0] = 1; bytes[18] = 0; bytes[19] = 1;
        bytes[21] = bytes[23] = 0x30;
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var packed = MzfCompressionService.Compress(new MZTFileReader().ReadMzfRecord(reader),
            new(MzfCompressionAlgorithm.Zx7, CompressionDirection.Backward, Zx7EmbeddedLoader: true), CompressionTarget.MzfTape).Record;
        var valid = new NativeComDialog(parent, packed);
        valid.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var validOutput = Children<CompressionOptionsControl>(valid).Single();
        var validCombo = (ComboBox)validOutput.FindName("algorithmComboBox");
        var export = Children<Button>(valid).Single(b => Equals(b.Content, "Export / Save..."));
        foreach (int index in new[] { 0, 1, 2, 3, 4 })
        {
            validCombo.SelectedIndex = index;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!export.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(20);
            Check(export.IsEnabled, "Asynchronous COM conversion finishes for policy index " + index);
            Check(Children<TextBlock>(valid).Any(t => t.Text == "Source compression: ZX7 · Backward · Embedded loader"), "Source status survives asynchronous conversion");
        }
        // Rapid changes also exercise cancellation/versioning before the final None result.
        validCombo.SelectedIndex = 2; validCombo.SelectedIndex = 4; validCombo.SelectedIndex = 1;
        var finalDeadline = DateTime.UtcNow.AddSeconds(15);
        while (!export.IsEnabled && DateTime.UtcNow < finalDeadline) await Task.Delay(20);
        Check(export.IsEnabled && validCombo.SelectedIndex == 1, "Rapid policy changes finish with the selected result");
        valid.Close();
        var header = packed.Header; header.MzfFtype = 2; packed.Header = header;
        var dialog = new NativeComDialog(parent, packed);
        dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var texts = Children<TextBlock>(dialog).ToArray();
        var source = texts.Single(t => t.Text == "Source compression: ZX7 · Backward · Embedded loader");
        var output = Children<CompressionOptionsControl>(dialog).Single();
        Check(output.KeepSource, "Dialog starts with KeepSource for selected TapeRecord");
        Check(source.TransformToAncestor(dialog).Transform(new Point()).Y < output.TransformToAncestor(dialog).Transform(new Point()).Y, "Source encoding appears above output policy");
        ((ComboBox)output.FindName("algorithmComboBox")).SelectedIndex = 1;
        Check(source.Text == "Source compression: ZX7 · Backward · Embedded loader", "Output policy does not change source status");
        dialog.Close();
        var empty = new NativeComDialog(parent); empty.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(Children<TextBlock>(empty).Any(t => t.Text == "Source compression: —"), "Empty dialog clears source status");
        empty.Close();
        Console.WriteLine("PASS: COM labels, selection/event stability, availability, other targets, selected TapeRecord, unsupported type and empty input.");
    }
    [STAThread]
    private static int Main()
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown }; int result=1;
        app.Resources["TopToolbarButton"] = new Style(typeof(Button));
        app.Resources["TopToolbarSeparator"] = new Style(typeof(Border));
        app.Startup+=async (_,_)=> {
            try {
                var source=DskDocumentFactory.CreateCpm(false); var target=DskDocumentFactory.CreateCpm(false);
                source.FileSystem.Insert("GAME.COM",[1,2,3]); source.FileSystem.Insert("TOOLS.COM",new byte[16384]);
                source.MarkModified();
                var sourceBytes=source.Serialize(); var targetBytes=target.Serialize();
                var preview=CrossDiskTransferService.Preview(source,target,source.FileSystem.ReadDirectory().Select(e=>new DiskCopyRequest(e.Key)).ToArray(),DiskCollisionPolicy.Cancel);
                var parent=new Window { Width=800,Height=600 }; parent.Show();
                CheckBatchOperations(parent);
                await CheckNativeCom(parent);
                CheckDskOpen();
                var dialog=new DskPatchPreviewWindow(parent,preview.Operation.Report,()=>preview.Operation.Apply(target),"Cross-disk copy preview","Apply to current disk",preview.Presentation());
                dialog.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var view=Children<ReportView>(dialog).Single();
                Check(!Children<TextBox>(view).Any(),"Reports must not expose a raw text surface");
                var tabs=Children<TabControl>(view).Single(); var files=(DataGrid)((TabItem)tabs.Items[0]).Content;
                Check(files.Items.Count==2 && files.Columns.Count==7,"File table / columns");
                Check(files.IsReadOnly && files.CanUserResizeColumns && files.CanUserSortColumns,"Read-only, resize, sorting");
                Check(files.ClipboardCopyMode==DataGridClipboardCopyMode.IncludeHeader,"Copy headers");
                Check(view.ExportContent(1)==preview.Operation.Report,"Exact text export retained");
                Check(view.ExportContent(2).Contains("Source file"),"Structured CSV export");
                Check(view.ExportContent(3).Contains("File properties"),"Structured JSON export");
                var firstCell=files.Columns[0].GetCellContent(files.Items[0]) as TextBlock;
                Check(firstCell!=null && firstCell.Text.EndsWith(".COM"),"Actual WPF cell binding");
                files.Columns[0].Width=250; files.SelectedItem=files.Items[0];
                tabs.SelectedIndex=1; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(!Children<TextBox>(view).Any(),"Metadata uses table"); tabs.SelectedIndex=0;
                dialog.Width=1100; dialog.Height=720; dialog.UpdateLayout();
                var artifact=Path.Combine(Path.GetTempPath(),"MZTools-ReportUiSmoke"); Directory.CreateDirectory(artifact);
                var bitmap=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(dialog);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using(var stream=File.Create(Path.Combine(artifact,"cross-disk-copy.png"))) encoder.Save(stream);
                Check(targetBytes.SequenceEqual(target.Serialize()),"Preview remains read-only");
                Children<Button>(dialog).Single(b=>Equals(b.Content,"Apply to current disk")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(dialog.Applied && target.FileSystem.ReadDirectory().Count==2,"Apply remains functional");
                Check(sourceBytes.SequenceEqual(source.Serialize()),"Source preserved");
                bool applied=false; var canceled=new DskPatchPreviewWindow(parent,"Warnings:\nReview only",()=>applied=true); canceled.Show();
                Children<Button>(canceled).Single(b=>Equals(b.Content,"Close")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!applied && !canceled.Applied,"Close does not apply");
                parent.Close(); Console.WriteLine("PASS: file/property tables, actual cell bindings, resize/sort/copy, no raw report TextBox, TXT/CSV/JSON, Apply/Close and source preservation.");
                Console.WriteLine("Screenshot: "+Path.Combine(artifact,"cross-disk-copy.png")); result=0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        };
        app.Run(); return result;
    }
}
