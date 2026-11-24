using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Smoke;

internal static class SavePickerScenario
{
    private static readonly object logGate=new();
    private static string trace="";
    private static void Log(string stage,object? data=null)
    {lock(logGate)File.AppendAllText(trace,JsonSerializer.Serialize(new{stage,data,utc=DateTime.UtcNow})+"\n");}
    internal static async Task Run(MainWindow owner,IClassicDesktopStyleApplicationLifetime desktop)
    {
        trace=Path.Combine(Program.Output,"save-picker-trace.jsonl");int exit=0;
        try
        {
            if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("This native smoke specifically requires Windows.");
            string source=Path.Combine(Program.Output,"source.mzf");var bytes=new byte[144];bytes[0]=1;bytes[1]=(byte)'T';bytes[17]=13;bytes[18]=16;bytes[21]=0x40;bytes[23]=0x40;File.WriteAllBytes(source,bytes);
            if(!await owner.Model.Documents.OpenPathAsync(source))throw new InvalidOperationException(owner.Model.Documents.Error);
            var tape=owner.Model.ActiveTape!;
            using var multi=new MemoryStream();
            for(int index=0;index<32;index++)
            {
                var record=new byte[128+8192];Array.Copy(bytes,record,128);record[18]=0;record[19]=32;
                System.Text.Encoding.ASCII.GetBytes("TEST"+index).CopyTo(record,1);
                for(int n=128;n<record.Length;n++)record[n]=(byte)(n+index);
                multi.Write(record);
            }
            string mzt=Path.Combine(Program.Output,"multi.mzt");File.WriteAllBytes(mzt,multi.ToArray());
            if(!await owner.Model.Documents.OpenPathAsync(mzt))throw new InvalidOperationException(owner.Model.Documents.Error);
            tape=owner.Model.ActiveTape!;
            for(int i=0;i<tape.Rows.Count;i++)if(i%4!=0)tape.EditInline(tape.Rows[i],speed:"1:"+(i%4+1));
            owner.Width=1706.6666666666667;owner.Height=937.3333333333334;
            tape.SetSelection(tape.Rows);long revision=tape.Revision;
            Log("multi-context",new{records=tape.Rows.Count,selected=tape.Selection.Count});
            owner.Model.Documents.PropertyChanged+=(_,e)=>{if(e.PropertyName is "IsBusy" or "IsAwaitingInput" or "Phase")Log("state",new{e.PropertyName,owner.Model.Documents.IsBusy,owner.Model.Documents.IsAwaitingInput,owner.Model.Documents.Phase});};
            Dispatcher.UIThread.UnhandledException+=(_,e)=>Log("unhandled",e.Exception.ToString());
            var mztTape=tape;var quickDisk=TapeSession.New(true);var imported=await MediaOperations.ImportAsync(quickDisk,[source,source],CancellationToken.None);quickDisk.ApplyRecords(imported.Records);owner.Model.Documents.Add(quickDisk);
            await VerifyNativeOwnerRecovery(owner);
            foreach(var context in new[]{mztTape,quickDisk})
            {
            tape=context;owner.Model.Documents.Active=tape;
            var table=owner.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="RecordsTable");
            foreach(var row in tape.Rows)if(!table.SelectedItems!.Contains(row))table.SelectedItems.Add(row);
            revision=tape.Revision;bool modified=tape.IsModified;
            foreach(bool separate in new[]{false,true})
            foreach(var format in new[]{"Wav","L16","Lep","Mzf","Mzt"})
            for(int iteration=0;iteration<(!tape.IsQuickDisk&&format is "Wav" or "Lep" or "L16"?10:1);iteration++)
            {
                int kind=iteration%3;bool submitted=false;int ticks=0;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(80)};
                timer.Tick+=(_,_)=>
                {
                    ticks++;
                    foreach(var dialog in owner.OwnedWindows.ToArray())
                    {
                        if(dialog.Title!="Export options"||submitted)continue;
                        var choice=dialog.GetVisualDescendants().OfType<ComboBox>().Single(c=>AutomationProperties.GetName(c)=="Output format");
                        choice.SelectedItem=choice.Items.OfType<FormatChoice>().Single(c=>c.Key==format);
                        dialog.GetVisualDescendants().OfType<CheckBox>().Single(c=>Equals(c.Content,"Separate file for each record")).IsChecked=separate;
                        dialog.Closed+=(_,_)=>Log("options-closed");submitted=true;Log("options-continue",format);
                        dialog.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Continue")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                };
                using var stop=new CancellationTokenSource();
                int pid=Environment.ProcessId;bool nativeSeen=false;
                var native=Task.Run(async()=>
                {
                    var deadline=DateTime.UtcNow.AddSeconds(25);
                    while(!stop.IsCancellationRequested)
                    {
                        if(!nativeSeen&&Native.Cancel(pid,kind)){nativeSeen=true;Log("native-cancel-sent",new{format,iteration,kind});}
                        if(DateTime.UtcNow>deadline){Log("watchdog-failed",new{format,iteration,submitted,nativeSeen,ticks});Environment.Exit(3);}
                        try{await Task.Delay(50,stop.Token);}catch(OperationCanceledException){break;}
                    }
                });
                timer.Start();Log("execute",new{format,iteration,kind,separate,quickDisk=tape.IsQuickDisk,speedEditors=owner.GetVisualDescendants().OfType<ComboBox>().Count(c=>AutomationProperties.GetName(c)=="Tape speed")});
                try
                {
                    owner.Model.ExportCommand.Execute(null);await owner.Model.Documents.WaitForIdleAsync();
                    await Dispatcher.UIThread.InvokeAsync(()=>{},DispatcherPriority.Background);
                    if(!nativeSeen||owner.Model.Documents.IsBusy||owner.Model.Documents.IsWorking||owner.Model.Documents.IsAwaitingInput||!owner.Model.ExportCommand.CanExecute(null)||!owner.IsEnabled||!Native.IsWindowEnabled(owner.TryGetPlatformHandle()!.Handle))throw new InvalidOperationException("Cancel did not restore the window and command state.");
                    if(tape.Revision!=revision||tape.IsModified!=modified||!File.ReadAllBytes(source).SequenceEqual(bytes)||!File.ReadAllBytes(mzt).SequenceEqual(multi.ToArray()))throw new InvalidOperationException("Cancel changed the source.");
                    Log("case-pass",new{format,iteration,kind,ticks,separate,quickDisk=tape.IsQuickDisk,selection=tape.Selection.Count});
                }
                finally{timer.Stop();stop.Cancel();await native;}
            }
            }
            Log("pass");
        }
        catch(Exception error){Log("failed",error.ToString());exit=1;}
        finally{desktop.Shutdown(exit);}
    }
    private static async Task VerifyNativeOwnerRecovery(MainWindow owner)
    {
        // The live VS Code process was idle with no dialog, but its HWND was disabled.
        nint handle=owner.TryGetPlatformHandle()!.Handle;
        foreach(bool open in new[]{false,true})
        for(int failure=0;failure<3;failure++)
        {
            int kind=failure;
            Task<T> Return<T>(T result)
            {
                Native.EnableWindow(handle,false);
                return kind==1?Task.FromException<T>(new OperationCanceledException()):kind==2?Task.FromException<T>(new IOException("Injected picker error")):Task.FromResult(result);
            }
            var dialogs=new DesktopDialogs(owner)
            {
                OutputPicker=_=>Return<Avalonia.Platform.Storage.IStorageFile?>(null),
                InputPicker=_=>Return<IReadOnlyList<string>>([])
            };
            try
            {
                if(open)await dialogs.OpenInputPickerAsync(new());else await dialogs.SaveOutputPickerAsync(new());
                if(failure!=0)throw new InvalidOperationException("Picker error was swallowed.");
            }
            catch(Exception e)when(failure==1&&e is OperationCanceledException||failure==2&&e is IOException){}
            if(!Native.IsWindowEnabled(handle))throw new InvalidOperationException("Completed native picker left its owner disabled (live-process regression).");
            Log("stale-native-owner-pass",new{open,failure});
        }
        var pending=new TaskCompletionSource<Avalonia.Platform.Storage.IStorageFile?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingDialogs=new DesktopDialogs(owner){OutputPicker=_=>{Native.EnableWindow(handle,false);return pending.Task;}};
        var waiting=waitingDialogs.SaveOutputPickerAsync(new());
        if(Native.IsWindowEnabled(handle))throw new InvalidOperationException("Picker recovery ran before the picker returned.");
        pending.SetResult(null);await waiting;
        if(!Native.IsWindowEnabled(handle))throw new InvalidOperationException("Owner was not restored after the pending picker returned.");
        Log("native-owner-pending-guard-pass");
        var modal=new Window{Title="Modal guard regression",Width=320,Height=180};
        var modalTask=modal.ShowDialog(owner);
        try
        {
            var dialogs=new DesktopDialogs(owner){OutputPicker=_=>Task.FromResult<Avalonia.Platform.Storage.IStorageFile?>(null)};
            await dialogs.SaveOutputPickerAsync(new());
            if(Native.IsWindowEnabled(handle))throw new InvalidOperationException("Picker recovery enabled an owner with a real modal dialog.");
            Log("native-owner-modal-guard-pass");
        }
        finally{modal.Close();await modalTask;}
        owner.IsEnabled=false;Native.EnableWindow(handle,false);
        try
        {
            var dialogs=new DesktopDialogs(owner){OutputPicker=_=>Task.FromResult<Avalonia.Platform.Storage.IStorageFile?>(null)};
            await dialogs.SaveOutputPickerAsync(new());
            if(Native.IsWindowEnabled(handle))throw new InvalidOperationException("Picker recovery overrode an explicitly disabled owner.");
            Log("native-owner-disabled-guard-pass");
        }
        finally{owner.IsEnabled=true;Native.EnableWindow(handle,true);}
    }
    private static class Native
    {
        private delegate bool Callback(nint hwnd,nint data);
        [DllImport("user32.dll")]private static extern bool EnumWindows(Callback callback,nint data);
        [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint hwnd,System.Text.StringBuilder name,int count);
        [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")]internal static extern bool IsWindowEnabled(nint hwnd);
        [DllImport("user32.dll")]internal static extern bool EnableWindow(nint hwnd,bool enabled);
        [DllImport("user32.dll")]private static extern bool PostMessage(nint hwnd,uint message,nint wparam,nint lparam);
        internal static bool Cancel(int process,int kind)
        {
            bool found=false;
            EnumWindows((hwnd,_)=>{GetWindowThreadProcessId(hwnd,out uint pid);var name=new System.Text.StringBuilder(80);GetClassName(hwnd,name,80);
                if(pid==process&&name.ToString()=="#32770"&&IsWindowVisible(hwnd)){found=true;if(kind==0)PostMessage(hwnd,0x0111,2,0);else if(kind==1){PostMessage(hwnd,0x0100,0x1b,0);PostMessage(hwnd,0x0101,0x1b,0);}else PostMessage(hwnd,0x0010,0,0);}return true;},0);
            return found;
        }
    }
}
