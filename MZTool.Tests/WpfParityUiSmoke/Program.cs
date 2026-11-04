using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MZTools;

internal static class Program
{
    static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    static object? Call(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    [STAThread]static int Main(string[] args)
    {
        if(args.Length!=1)return 2;
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);int exit=1;var frames=new List<object>();
        var app=new MZTools.App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        async Task Capture(Window window,string name)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);window.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(window);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(output,name)))encoder.Save(stream);
            frames.Add(new{file=name,logicalWidth=window.ActualWidth,logicalHeight=window.ActualHeight,scale=dpi.DpiScaleX,origin="Unmodified render of the retained WPF frontend"});
        }
        app.Startup+=async(_,_)=>
        {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var main=(MainWindow)app.MainWindow;await Capture(main,"wpf-original-empty.png");
                byte[] bytes=new byte[640];bytes[0]=1;System.Text.Encoding.ASCII.GetBytes("RENAMED").CopyTo(bytes,1);bytes[17]=13;BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18),512);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20),0x4000);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22),0x4000);for(int i=24;i<128;i++)bytes[i]=(byte)i;for(int i=128;i<bytes.Length;i++)bytes[i]=(byte)(i%7);
                string path=Path.Combine(output,"reference.mzf");File.WriteAllBytes(path,bytes);var document=Field<TapeDocument>(main,"document");document.Records.Add(new MZTFileReader().ReadStandaloneMzf(path));document.Format=TapeDocumentFormat.Mzf;document.FilePath=path;document.IsModified=false;Call(main,"RefreshGrid");await Capture(main,"wpf-original-tape.png");
                var disk=DskDocumentFactory.CreateFsmz(false);disk.FileSystem.Insert("FIRST",[1,2,3],1,0x4000,0x4000);disk.MarkModified();
                var editor=new DskEditorControl();var diskWindow=new Window{Title="WPF reference · DSK",Width=1320,Height=600,Content=editor};diskWindow.Show();editor.LoadDocument(disk);await Capture(diskWindow,"wpf-original-disk.png");
                int hardwareRequests=0;var recorder=new AudioRecorderWindow(null,_=>{hardwareRequests++;throw new InvalidOperationException("Physical input forbidden in reference smoke.");},true,()=>true,()=>false,_=>false);recorder.Show();await Capture(recorder,"wpf-original-recorder.png");
                recorder.Monitor.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Task.Delay(300);await Capture(recorder,"wpf-original-recorder-live.png");await recorder.Model.StopAsync();
                if(hardwareRequests!=0)throw new InvalidOperationException("Reference rendering requested hardware.");
                recorder.Close();diskWindow.Close();main.Close();exit=0;
            }
            catch(Exception e){Console.Error.WriteLine(e);}
            finally{File.WriteAllText(Path.Combine(output,"wpf-reference.json"),JsonSerializer.Serialize(new{exit,frames,hardwareTested=false},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown();}
        };
        app.Run();Console.WriteLine("WPF visual reference exit="+exit);return exit;
    }
}
