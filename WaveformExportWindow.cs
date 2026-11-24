using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal static class WaveformExportWindow
{
    internal static bool Run(Window owner,IReadOnlyList<TapeRecord> records,IReadOnlyList<string> paths,SharpTapeOutputFormat format,SharpTapeMachine machine,int sampleRate,TapeExportGapOptions gaps,string? activeSource)
    {
        gaps.Validate();
        var snapshot=records.Select(r=>r.DeepClone()).ToArray();
        var protectedPaths=snapshot.SelectMany(r=>r.SourceFingerprints?.Keys??Enumerable.Empty<string>()).ToHashSet(AtomicFileTransaction.PathComparer);
        if(activeSource is not null)protectedPaths.Add(Path.GetFullPath(activeSource));
        if(paths.Select(Path.GetFullPath).Any(protectedPaths.Contains))throw new IOException("Export must not replace the open source or its metadata.");
        using var cancellation=new CancellationTokenSource();bool finished=false,accepted=false;Exception? failure=null;
        var dialog=new Window{Owner=owner,Title="Export waveform",Width=510,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};
        var panel=new StackPanel{Margin=new Thickness(16)};dialog.Content=panel;
        string summary=(paths.Count==1?$"Combined output: {snapshot.Length} program(s).":"Separate files: one start/end pause per file; no between-file pause.")+"\n"+gaps.Description;
        var status=new TextBlock{Text=summary,TextWrapping=TextWrapping.Wrap};panel.Children.Add(status);
        panel.Children.Add(new ProgressBar{Height=6,IsIndeterminate=true,Margin=new Thickness(0,12,0,12)});
        var cancel=new Button{Content="Cancel",MinWidth=85,HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(cancel);
        void Stop(){if(finished||cancellation.IsCancellationRequested)return;cancel.IsEnabled=false;status.Text="Cancelling… Waiting for export cleanup.";cancellation.Cancel();}
        cancel.Click+=(_,_)=>Stop();dialog.Closing+=(_,e)=>{if(!finished){e.Cancel=true;Stop();}};
        var progress=new Progress<string>(text=>{if(!finished&&!cancellation.IsCancellationRequested)status.Text=text;});
        dialog.Loaded+=async(_,_)=>
        {
            try
            {
                await Task.Run(()=>
                {
                    var expected=paths.ToDictionary(Path.GetFullPath,p=>AtomicFileTransaction.HashCancellable(p,cancellation.Token),AtomicFileTransaction.PathComparer);
                    var sources=protectedPaths.ToDictionary(p=>p,p=>AtomicFileTransaction.HashCancellable(p,cancellation.Token),AtomicFileTransaction.PathComparer);
                    var outputs=new Dictionary<string,byte[]>(AtomicFileTransaction.PathComparer);var staged=new Dictionary<string,string>(AtomicFileTransaction.PathComparer);
                    try
                    {
                        for(int i=0;i<paths.Count;i++)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();((IProgress<string>)progress).Report($"Preparing {i+1}/{paths.Count} output(s).\n"+summary);
                            string path=Path.GetFullPath(paths[i]);string temporary=Path.Combine(Path.GetTempPath(),"mztools-waveform-"+Guid.NewGuid().ToString("N")+Path.GetExtension(path));staged.Add(path,temporary);outputs.Add(path,Array.Empty<byte>());
                            SharpTapeExporter.Export(temporary,paths.Count==1?snapshot:new[]{snapshot[i]},format,machine,sampleRate,cancellation.Token,gaps);
                        }
                        AtomicFileTransaction.Write(outputs,expected,cancellation.Token,sourceFiles:staged,beforeCommit:()=>{foreach(var source in sources)if(AtomicFileTransaction.HashCancellable(source.Key,cancellation.Token)!=source.Value)throw new IOException("The source changed during export: "+source.Key);});
                    }
                    finally{foreach(string temporary in staged.Values)if(File.Exists(temporary))File.Delete(temporary);}
                },cancellation.Token);
                accepted=true;
            }
            catch(OperationCanceledException){}
            catch(Exception error){failure=error;}
            finally{finished=true;dialog.DialogResult=accepted;}
        };
        dialog.ShowDialog();if(failure is not null)throw new IOException("Waveform export failed: "+failure.Message,failure);return accepted;
    }
}
