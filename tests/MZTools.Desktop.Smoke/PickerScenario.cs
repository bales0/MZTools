using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Smoke;

// Explicit, limited native-picker check. No corpus, recording hardware or full UI gallery.
internal static class PickerScenario
{
    internal static async Task Run(MainWindow owner,IClassicDesktopStyleApplicationLifetime desktop)
    {
        var checks=new List<object>();int exit=0;
        void Check(bool ok,string name){checks.Add(new{name,pass=ok});if(!ok)throw new InvalidOperationException(name);}
        try
        {
            await Task.Delay(200);
            byte[] mzf=new byte[144];mzf[0]=1;mzf[1]=(byte)'T';mzf[17]=13;
            BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(18),16);BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(20),0x4000);BinaryPrimitives.WriteUInt16LittleEndian(mzf.AsSpan(22),0x4000);
            string input=Path.Combine(Program.Output,"small.MZF");File.WriteAllBytes(input,mzf);
            string diskPath=Path.Combine(Program.Output,"small.DSK");using(var stream=System.Reflection.Assembly.Load("MZTools.Core").GetManifestResourceStream("MZTools.Boot.P-CPM80.dsk")!)using(var target=File.Create(diskPath))stream.CopyTo(target);
            async Task<T> Pick<T>(Func<Task<T>> action,string? selection)
            {
                bool seen=false,submitted=false,logged=false;int ticks=0;double? shownMilliseconds=null;var watch=System.Diagnostics.Stopwatch.StartNew();
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(200)};
                timer.Tick+=(_,_)=>
                {
                    ticks++;
                    if(OperatingSystem.IsWindows())
                    {
                        if(selection is null)seen|=NativePicker.CancelOwnedDialog(Environment.ProcessId);
                        else if(!submitted){submitted=NativeInput.Select(Environment.ProcessId,selection);seen|=submitted;}
                    }
                    foreach(var dialog in desktop.Windows.Where(w=>w!=owner&&w.Owner==owner).ToArray())
                    {
                        seen=true;
                        if(selection is null){dialog.Close();continue;}
                        if(submitted)continue;
                        if(!logged)checks.Add(new{name="Managed picker controls",controls=dialog.GetVisualDescendants().Where(c=>c is TextBox or Button).Select(c=>new{type=c.GetType().Name,c.Name,content=(c as Button)?.Content?.ToString()}).ToArray()});
                        logged=true;
                        var boxes=dialog.GetVisualDescendants().OfType<TextBox>().ToArray();
                        var open=dialog.GetVisualDescendants().OfType<Button>().FirstOrDefault(b=>b.Content?.ToString() is "Open" or "Select" or "OK");
                        // Managed fallback uses a file-name field; only touch the owned picker.
                        var box=boxes.FirstOrDefault(b=>b.Name?.Contains("FileName",StringComparison.OrdinalIgnoreCase)==true) ?? boxes.LastOrDefault(b=>b.Name is null);
                        if(box is not null&&open is not null){box.Text=selection;open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));submitted=true;}
                    }
                    if(seen&&shownMilliseconds is null)shownMilliseconds=watch.Elapsed.TotalMilliseconds;
                    if(ticks>100)
                    {
                        timer.Stop();
                        File.WriteAllText(Path.Combine(Program.Output,"picker-timeout-controls.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
                        foreach(var dialog in desktop.Windows.Where(w=>w!=owner&&w.Owner==owner).ToArray())dialog.Close();
                        if(OperatingSystem.IsWindows())NativePicker.CancelOwnedDialog(Environment.ProcessId);
                    }
                };
                timer.Start();try{var result=await action();checks.Add(new{name="OS picker timing",provider=owner.StorageProvider.GetType().FullName,selection=selection is null?"Cancel":Path.GetFileName(selection),shownMilliseconds,totalMilliseconds=watch.Elapsed.TotalMilliseconds});Check(seen,"Real picker displayed: "+(selection is null?"Cancel":Path.GetFileName(selection)));return result;}finally{timer.Stop();}
            }
            var dialogs=new DesktopDialogs(owner);
            Check(await Pick(()=>dialogs.OpenAsync(),null) is null,"Open Cancel");
            if(OperatingSystem.IsWindows())
            {
                Check(await Pick(()=>dialogs.OpenAsync(),input)==input,"Open selects uppercase MZF");
                var loaded=await new MediaDocumentLoader(dialogs).LoadAsync(input,CancellationToken.None);using(loaded)
                {
                    var tape=(TapeSession)loaded;
                    var paths=await Pick(()=>dialogs.PickImportAsync(tape),input);
                    var preview=await MediaOperations.ImportAsync(tape,paths,CancellationToken.None);
                    Check(preview.Report.StartsWith("Import 1 records",StringComparison.Ordinal)&&tape.Rows.Count==1,"Real import returns a preview only");
                    Check((await Pick(()=>dialogs.PickImportAsync(tape),null)).Count==0,"Import Cancel");
                }
                Check(await Pick(()=>dialogs.OpenAsync(),diskPath)==diskPath,"Open selects uppercase DSK");
            }
            else
            {
                Check((await Pick(()=>dialogs.PickImportAsync(TapeSession.New(false)),null)).Count==0,"Import Cancel");
                checks.Add(new{name="Linux picker file selection",status="NOT TESTED: managed fallback automation could not submit the filename; GTK/KDE provider unavailable"});
            }
            Check((await Pick(()=>dialogs.PickDiskFilesAsync(FilePickerPurpose.RawSectorImport,"Raw input"),null)).Count==0,"Raw picker Cancel");
            Check((await Pick(()=>dialogs.PickDiskFilesAsync(FilePickerPurpose.CompareDisk,"Compare disk"),null)).Count==0,"Compare picker Cancel");
            Check((await Pick(()=>dialogs.PickDiskFilesAsync(FilePickerPurpose.ApplyDiskPatch,"Apply patch"),null)).Count==0,"Patch picker Cancel");
            Check(await Pick(()=>new DesktopDialogs(owner){AudioOnly=true}.OpenAsync(),null) is null,"Audio picker Cancel");
            foreach(var purpose in new[]{FilePickerPurpose.ApplyDiskPatch,FilePickerPurpose.OpenAudio,FilePickerPurpose.OpenMedia})
            {
                var watch=System.Diagnostics.Stopwatch.StartNew();var options=FilePickerPolicy.Options(purpose,"Pattern instrumentation");watch.Stop();
                int patterns=options.FileTypeFilter!.Sum(g=>g.Patterns!.Count);
                checks.Add(new{name="Picker pattern budget",purpose=purpose.ToString(),patterns,optionsCreationMilliseconds=watch.Elapsed.TotalMilliseconds});
                Check(patterns<=64,"Bounded patterns: "+purpose);
            }
            using(var tape=(TapeSession)await new MediaDocumentLoader().LoadAsync(input,CancellationToken.None))
            {
                string wave=Path.Combine(Program.Output,"program.wav");
                using(var staged=await MediaOperations.ExportAsync(tape,new(wave,"Wav"),CancellationToken.None))File.Copy(staged.SourceFiles[wave],wave,true);
                var before=File.ReadAllBytes(wave);
                if(OperatingSystem.IsWindows())Check(await Pick(()=>dialogs.OpenAsync(),wave)==wave,"OS provider selects WAV with current Open filter");
                async Task<bool> Drive(Func<Task<bool>> work,bool cancel)
                {
                    bool optionsSeen=false;int ticks=0;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
                    timer.Tick+=(_,_)=>
                    {
                        ticks++;
                        foreach(var dialog in desktop.Windows.Where(w=>w!=owner&&w.Owner==owner).ToArray())
                        {
                            string? caption=null;
                            if(dialog.Title=="Audio import options")
                            {
                                if(!optionsSeen)
                                {
                                    Check(dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked==false,"Native import defaults to Standard");
                                    Check(dialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex==0,"Native import defaults to Summary");optionsSeen=true;
                                }
                                caption=cancel?"Cancel":"Continue";
                            }
                            else if(dialog.Title=="Audio program import report")caption="Close";
                            if(caption is not null)dialog.GetVisualDescendants().OfType<Button>().First(b=>Equals(b.Content,caption)).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        }
                        if(ticks>200){timer.Stop();foreach(var dialog in owner.OwnedWindows.ToArray())dialog.Close();}
                    };
                    timer.Start();try{bool result=await work();Check(optionsSeen,"Real audio options displayed");return result;}finally{timer.Stop();}
                }
                Check(!await Drive(()=>owner.Model.Documents.OpenPathAsync(wave),true),"Native Open options Cancel preserves documents");
                Check(await Drive(()=>owner.Model.Documents.OpenPathAsync(wave),false),"Native Open decodes tape programs");
                Check(owner.Model.Documents.Active is TapeSession {Rows.Count:1},"Main audio Open selects Tape, not PCM analysis");
                Check(await Drive(async()=>{var preview=await MediaOperations.ImportAsync(tape,[wave],CancellationToken.None,dialogs);return preview.Report.StartsWith("Import 1 records",StringComparison.Ordinal)&&tape.Rows.Count==1;},false),"Native audio Add returns preview without partial mutation");
                Check(File.ReadAllBytes(wave).SequenceEqual(before),"Native audio Open/Add/Cancel preserve source bytes");
            }
            Check(File.ReadAllBytes(input).SequenceEqual(mzf),"Native checks preserve source bytes");
        }
        catch(Exception error){checks.Add(new{name="Failure",pass=false,error=error.ToString()});exit=1;}
        finally
        {
            File.WriteAllText(Path.Combine(Program.Output,"picker-native.json"),JsonSerializer.Serialize(new{os=RuntimeInformation.OSDescription,provider=owner.StorageProvider.GetType().FullName,checks,exit,macOsTested=false},new JsonSerializerOptions{WriteIndented=true}));
            desktop.Shutdown(exit);
        }
    }
    private static class NativeInput
    {
        private delegate bool Callback(nint hwnd,nint data);
        [DllImport("user32.dll")]private static extern bool EnumWindows(Callback callback,nint data);
        [DllImport("user32.dll")]private static extern bool EnumChildWindows(nint owner,Callback callback,nint data);
        [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint hwnd,System.Text.StringBuilder value,int size);
        [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint SendMessage(nint hwnd,uint message,nint wparam,string value);
        [DllImport("user32.dll")]private static extern bool PostMessage(nint hwnd,uint message,nint wparam,nint lparam);
        private static string Class(nint hwnd){var value=new System.Text.StringBuilder(80);GetClassName(hwnd,value,80);return value.ToString();}
        internal static bool Select(int pid,string path)
        {
            bool submitted=false;
            EnumWindows((hwnd,_)=>
            {
                GetWindowThreadProcessId(hwnd,out uint owner);
                if(owner!=pid||Class(hwnd)!="#32770")return true;
                nint input=0;
                EnumChildWindows(hwnd,(child,_)=>{if(Class(child)=="Edit"&&IsWindowVisible(child))input=child;return true;},0);
                if(input!=0){SendMessage(input,0x000C,0,path);PostMessage(hwnd,0x0111,1,0);submitted=true;}
                return true;
            },0);
            return submitted;
        }
    }
}
