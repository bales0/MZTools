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
    [STAThread]
    private static int Main()
    {
        var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown }; int result=1;
        app.Startup+=async (_,_)=> {
            try {
                var source=DskDocumentFactory.CreateCpm(false); var target=DskDocumentFactory.CreateCpm(false);
                source.FileSystem.Insert("GAME.COM",[1,2,3]); source.FileSystem.Insert("TOOLS.COM",new byte[16384]);
                source.MarkModified();
                var sourceBytes=source.Serialize(); var targetBytes=target.Serialize();
                var preview=CrossDiskTransferService.Preview(source,target,source.FileSystem.ReadDirectory().Select(e=>new DiskCopyRequest(e.Key)).ToArray(),DiskCollisionPolicy.Cancel);
                var parent=new Window { Width=800,Height=600 }; parent.Show();
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
