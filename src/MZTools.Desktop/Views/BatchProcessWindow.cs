using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

/// <summary>Persistent WPF Batch workflow; Core owns all format and transaction rules.</summary>
public sealed class BatchProcessWindow : Window
{
    internal sealed record OperationChoice(BatchOperation Value){public override string ToString()=>BatchMediaPolicy.Label(Value);}
    public ShellViewModel Model {get;}
    private readonly DesktopDialogs dialogs;
    private readonly StackPanel parameters=new(){Spacing=0},actions=new(){Orientation=Orientation.Horizontal,Spacing=6};
    private readonly ComboBox operation=new(){Name="BatchOperation",ItemsSource=new[]{BatchOperation.TestIntegrity}.Concat(Enum.GetValues<BatchOperation>().Where(o=>o is not (BatchOperation.TestIntegrity or BatchOperation.Analyze))).Append(BatchOperation.Analyze).Select(o=>new OperationChoice(o)).ToArray(),SelectedIndex=0};
    private readonly TextBlock hint=new(){Name="OperationHint",TextWrapping=TextWrapping.Wrap};
    private readonly RadioButton filesMode=new(){Name="SelectedFilesMode",Content="Selected files",GroupName="BatchInput",IsChecked=true},folderMode=new(){Name="InputFolderMode",Content="Input folder",GroupName="BatchInput"};
    private readonly TextBox paths=new(){Name="SelectedFilePaths",IsReadOnly=true},folder=new(){Name="InputFolder"},extensions=new(){Name="ExtensionFilter"},auxiliary=new(){Name="BootSource"},output=new(){Name="OutputFolder",Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"MZTools Batch")},naming=new(){Name="NamingTemplate",Text="{name}_processed{ext}"},sameSource=new(){Text="Same as source",IsReadOnly=true,IsEnabled=false};
    private readonly ComboBox target=new(){Name="ConversionTarget",ItemsSource=BatchMediaPolicy.Targets.Distinct().ToArray(),SelectedIndex=0},extract=new(){Name="ExtractFormat",ItemsSource=BatchProcessForm.ExtractionFormats,SelectedIndex=0},rate=new(){Name="AudioSampleRate",ItemsSource=new[]{44100,22050},SelectedIndex=0};
    private readonly ComboBox compressionMode=new(){Name="CompressionMode",ItemsSource=Enum.GetNames<MzfCompressionAlgorithm>(),SelectedItem="Auto",ItemTemplate=new FuncDataTemplate<string>((value,_)=>new TextBlock{Text=value switch{"Auto"=>"Auto (smallest safe)","Zx0"=>"ZX0","Zx7"=>"ZX7",_=>value}})};
    private readonly StackPanel direction=new(){Name="CompressionDirection",Orientation=Orientation.Horizontal,Spacing=16};
    private readonly RadioButton forward=new(){Content="Forward",GroupName="BatchCompressionDirection",IsChecked=true},backward=new(){Content="Backward",GroupName="BatchCompressionDirection"};
    private readonly CheckBox recursive=new(){Content="Recursive folder scan"},strict=new(){Content="Strict all-or-nothing preflight (any invalid/incompatible input blocks execution)"},replace=new(){Content="Replace originals (requires explicit confirmation)"},backup=new(){Content="Keep .bak when replacing originals",IsChecked=true},heuristic=new(){Name="UseHeuristicAnalysis",Content="Use heuristic analysis (WAV / FLAC)"},quick=new(){Content="ZX0 quick"},embedded=new(){Content="ZX7 embedded loader"};
    private readonly StackPanel compression=new(){Name="BatchCompression",Spacing=4};
    private readonly TextBlock compressionHint=new(){Text="Compression works on decoded program data, including recognized packed input. Batch output requires Skip bytes = 0. Audio input produces an MZT; QD input keeps its container.",TextWrapping=TextWrapping.Wrap};
    private readonly Button add=new(){Content="Add files..."},browseFolder=new(){Content="Browse..."},browseAux=new(){Content="Browse..."},browseOutput=new(){Content="Browse..."},clear=new(){Name="ClearFileList",Content="Clear file list"},preview=new(){Content="Preview / dry-run"},execute=new(){Content="Execute preview"},details=new(){Content="Selected details..."},report=new(){Content="Export TXT / CSV / JSON report..."},stop=new(){Name="StopBatch",Content="Stop",HorizontalAlignment=HorizontalAlignment.Right};
    private readonly TableView table=new(){Name="InputFileList",SelectionMode=SelectionMode.Multiple,AutoScrollToSelectedItem=true};
    private readonly TextBlock summary=new(){Name="BatchSummary",TextWrapping=TextWrapping.Wrap},error=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brushes.Firebrick};
    private readonly ProgressBar progress=new(){Name="BatchProgress",Height=6,Minimum=0,Maximum=1};
    private readonly List<string> selected=[];
    private CancellationTokenSource? scanCancellation;private Task scan=Task.CompletedTask;
    private readonly List<Task> pendingScans=[];
    private bool updating,current,closed,closeApproved,closeRequested;
    private int version;private string? previousExtensions;
    internal BatchOperation Operation=>((OperationChoice)operation.SelectedItem!).Value;
    internal BatchSession? Session=>Model.ActiveBatch;
    internal TableView InputTable=>table;
    internal Task PendingScan=>scan;
    public BatchProcessWindow()
    {
        Title="Batch Process";Width=1120;Height=800;MinWidth=800;MinHeight=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        dialogs=new DesktopDialogs(this);Model=new(new(new MediaDocumentLoader(dialogs),dialogs,new()),async text=>{if(Clipboard is{} clipboard)await clipboard.SetTextAsync(text);},dialogs);
        DataContext=Model;DesktopWindowSizing.FitToWorkingArea(this);
        var root=new Grid{RowDefinitions=new("Auto,*,Auto"),Margin=new Thickness(12),RowSpacing=6};Content=root;
        var scroll=new ScrollViewer{Content=parameters,MaxHeight=480,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};root.Children.Add(scroll);
        void Row(string label,Control editor,Button? browse=null,StackPanel? parent=null)
        {
            var row=new Grid{ColumnDefinitions=new("150,*,120"),Margin=new Thickness(0,3)};row.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center});
            editor.HorizontalAlignment=HorizontalAlignment.Stretch;Grid.SetColumn(editor,1);row.Children.Add(editor);AutomationProperties.SetName(editor,label);if(browse is not null){browse.Margin=new Thickness(8,0,0,0);Grid.SetColumn(browse,2);row.Children.Add(browse);} (parent??parameters).Children.Add(row);
        }
        Row("Operation",operation);parameters.Children.Add(hint);
        parameters.Children.Add(new StackPanel{Orientation=Orientation.Horizontal,Spacing=20,Children={filesMode,folderMode}});
        Row("Selected files",paths,add);Row("Input folder",folder,browseFolder);Row("Folder extensions",extensions);parameters.Children.Add(recursive);
        var targets=new Grid();targets.Children.Add(target);targets.Children.Add(sameSource);Row("Conversion target",targets);
        Row("Extract output format",extract);var compressionOptions=new StackPanel{Spacing=6};compressionOptions.Children.Add(new TextBlock{Text="Compression",FontWeight=FontWeight.SemiBold});Row("Algorithm",compressionMode,parent:compressionOptions);direction.Children.Add(forward);direction.Children.Add(backward);Row("Direction",direction,parent:compressionOptions);compressionOptions.Children.Add(quick);compressionOptions.Children.Add(embedded);compressionOptions.Children.Add(new TextBlock{Text="Skip bytes = 0"});compression.Children.Add(new Border{Child=compressionOptions,BorderBrush=Brushes.Gray,BorderThickness=new Thickness(1),Padding=new Thickness(10),Margin=new Thickness(0,12,0,0)});parameters.Children.Add(compression);parameters.Children.Add(compressionHint);
        Row("WAV / FLAC rate (Hz)",rate);parameters.Children.Add(heuristic);Row("Boot source / profile",auxiliary,browseAux);Row("Output folder",output,browseOutput);Row("Naming template",naming);foreach(var box in new[]{strict,replace,backup})parameters.Children.Add(box);
        var center=new DockPanel();Grid.SetRow(center,1);root.Children.Add(center);var header=new DockPanel();DockPanel.SetDock(header,Dock.Top);center.Children.Add(header);DockPanel.SetDock(clear,Dock.Right);header.Children.Add(clear);header.Children.Add(new TextBlock{Text="Selected inputs / preview results",FontWeight=FontWeight.SemiBold});center.Children.Add(table);
        foreach(var (caption,path) in new[]{("Input","Input"),("DetectedFormat","Format"),("Filesystem","Filesystem"),("Operation","Operation"),("Result","Result"),("Integrity","Integrity"),("Reason","Reason"),("Output","Output")})table.Columns.Add(new(){Header=caption,Binding=new Binding(path),Width=new GridLength(caption is "Input" or "Output" or "Reason"?230:135)});
        DskTableBehavior.Configure(table);AutomationProperties.SetName(table,"Selected inputs / preview results");
        var footer=new StackPanel{Spacing=4};Grid.SetRow(footer,2);root.Children.Add(footer);footer.Children.Add(summary);footer.Children.Add(error);footer.Children.Add(progress);footer.Children.Add(actions);foreach(var button in new[]{preview,execute,details,report})actions.Children.Add(button);footer.Children.Add(stop);
        SizeChanged+=(_,_)=>scroll.MaxHeight=Math.Max(90,Bounds.Height-footer.Bounds.Height-220);
        foreach(var box in new[]{extensions,auxiliary,output,naming})box.TextChanging+=(_,_)=>Invalidate();
        folder.TextChanging+=(_,_)=>{if(!updating&& !string.IsNullOrWhiteSpace(folder.Text))folderMode.IsChecked=true;Invalidate();};
        foreach(var box in new[]{recursive,strict,replace,backup,heuristic,quick,embedded})box.IsCheckedChanged+=(_,_)=>Invalidate();
        foreach(var box in new[]{operation,target,extract,rate,compressionMode})box.SelectionChanged+=(_,_)=>Invalidate();forward.IsCheckedChanged+=(_,_)=>Invalidate();backward.IsCheckedChanged+=(_,_)=>Invalidate();
        filesMode.IsCheckedChanged+=(_,_)=>{if(filesMode.IsChecked==true)SwitchMode();};folderMode.IsCheckedChanged+=(_,_)=>{if(folderMode.IsChecked==true)SwitchMode();};
        add.Click+=async(_,_)=>{var files=await dialogs.PickBatchFilesAsync("Batch inputs",BatchMediaPolicy.Extensions(Operation,Target));AddInputs(files);};
        browseFolder.Click+=async(_,_)=>{if(await PickFolder("Input folder") is{} path)folder.Text=path;};browseOutput.Click+=async(_,_)=>{if(await PickFolder("Output folder") is{} path)output.Text=path;};
        browseAux.Click+=async(_,_)=>{var files=await dialogs.OpenInputPickerAsync(FilePickerPolicy.Options(FilePickerPurpose.BootSystemOrProfile,"Boot DSK / boot profile"));if(files.FirstOrDefault() is{} path)auxiliary.Text=path;};
        clear.Click+=(_,_)=>{updating=true;selected.Clear();folder.Text="";filesMode.IsChecked=true;updating=false;Invalidate();};
        preview.Click+=async(_,_)=>await PreviewAsync();execute.Click+=async(_,_)=>await ExecuteAsync();details.Click+=(_,_)=>OpenDetails();table.DoubleTapped+=(_,_)=>OpenDetails();table.SelectionChanged+=(_,_)=>States();report.Click+=async(_,_)=>await ExportAsync();stop.Click+=(_,_)=>{Model.Documents.Cancel();summary.Text="Stopping… Completed outputs are kept; pending work will be cancelled.";};
        Model.Documents.PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName==nameof(DocumentCoordinator.Status)&&Model.Documents.IsBusy)summary.Text=Model.Documents.Status;
            if(e.PropertyName is nameof(DocumentCoordinator.ProgressFraction) or nameof(DocumentCoordinator.HasProgress)){progress.Value=Model.Documents.ProgressFraction;progress.IsIndeterminate=!Model.Documents.HasProgress;}
            if(e.PropertyName==nameof(DocumentCoordinator.IsBusy))States();error.Text=Model.Documents.Error;
        };
        Closing+=(_,e)=>{if(closeApproved)return;e.Cancel=true;if(closeRequested)return;closeRequested=true;States();Model.Documents.Cancel();scanCancellation?.Cancel();foreach(var child in OwnedWindows.ToArray())child.Close();_=CloseSafely();};
        Closed+=(_,_)=>{closed=true;version++;scanCancellation?.Cancel();scanCancellation?.Dispose();};Invalidate();
    }
    private string Target=>(string)target.SelectedItem!;
    private void SwitchMode()
    {
        if(updating)return;updating=true;if(folderMode.IsChecked==true)selected.Clear();else folder.Text="";updating=false;Invalidate();
    }
    internal void AddInputs(IEnumerable<string> inputs)
    {
        if(Model.Documents.IsBusy)return;updating=true;filesMode.IsChecked=true;folder.Text="";foreach(var path in inputs.Select(Path.GetFullPath))if(!selected.Contains(path,AtomicFileTransaction.PathComparer))selected.Add(path);updating=false;Invalidate();
    }
    private void Invalidate()
    {
        if(updating||closed)return;version++;current=false;Session?.Invalidate();scanCancellation?.Cancel();updating=true;
        try
        {
            string recommendation=BatchMediaPolicy.Extensions(Operation,Target);if(previousExtensions is null||extensions.Text==previousExtensions||string.IsNullOrWhiteSpace(extensions.Text))extensions.Text=recommendation;previousExtensions=recommendation;ToolTip.SetTip(extensions,"Folder scan filter. This operation/target accepts: "+recommendation);
            bool convert=Operation==BatchOperation.ConvertFormat,audio=BatchProcessForm.ProducesAudio(Operation,Target,(string)extract.SelectedItem!),writes=!BatchMediaPolicy.ReadOnly(Operation),tape=Operation is BatchOperation.CompressPrograms or BatchOperation.DecompressPrograms||convert&&BatchTapeService.Targets.Contains(Target);
            target.IsVisible=target.IsEnabled=convert;sameSource.IsVisible=!convert;sameSource.Text=Operation is BatchOperation.CompressPrograms or BatchOperation.DecompressPrograms?"Program container (MZF/M12; MZT for audio; same QD container)":"Same as source";
            extract.IsEnabled=Operation==BatchOperation.ExtractAll;rate.IsEnabled=audio;heuristic.IsEnabled=BatchProcessForm.SupportsAudioInput(Operation,Target);
            compression.IsVisible=Operation==BatchOperation.CompressPrograms;compressionHint.IsVisible=Operation is BatchOperation.CompressPrograms or BatchOperation.DecompressPrograms;
            quick.IsEnabled=Equals(compressionMode.SelectedItem,"Zx0");embedded.IsEnabled=Equals(compressionMode.SelectedItem,"Zx7");direction.IsEnabled=Equals(compressionMode.SelectedItem,"Zx0")||Equals(compressionMode.SelectedItem,"Zx7");
            if(!quick.IsEnabled)quick.IsChecked=false;if(!embedded.IsEnabled)embedded.IsChecked=false;
            auxiliary.IsEnabled=browseAux.IsEnabled=Operation==BatchOperation.InstallBootSystem;output.IsEnabled=browseOutput.IsEnabled=naming.IsEnabled=writes;replace.IsEnabled=writes&&!tape&&Operation!=BatchOperation.ExtractAll;if(!replace.IsEnabled)replace.IsChecked=false;backup.IsEnabled=replace.IsEnabled&&replace.IsChecked==true;
            bool fromFolder=folderMode.IsChecked==true;folder.IsEnabled=browseFolder.IsEnabled=extensions.IsEnabled=recursive.IsEnabled=paths.IsEnabled=add.IsEnabled=true;
            hint.Text=Operation switch{BatchOperation.TestIntegrity=>"Lists contents, sizes, addresses and metadata, and tests available structure, CRC/checksums and recognized compressed streams in the same run. See Integrity checks for results and format limits. MZF/M12 have no payload checksum.",BatchOperation.ExtractAll=>"Export each file/program into its own input's output directory. Native keeps disk filenames/data and exports tape programs as MZF. Tape/audio formats require stored type/LOAD/EXEC metadata; CP/M raw files use Native or BIN.",_=>"Preview prepares and verifies the selected operation before any user outputs are written."};
            paths.Text=selected.Count switch{0=>"No files selected",1=>selected[0],_=>$"{selected.Count} files selected — see list below"};ToolTip.SetTip(paths,string.Join('\n',selected));
            ShowInputs(fromFolder?[]:selected);summary.Text=$"Selected files: {selected.Count}. Preview to validate the operation.";error.Text="";
            pendingScans.RemoveAll(task=>task.IsCompleted);
            if(fromFolder&&!string.IsNullOrWhiteSpace(folder.Text)){scanCancellation=new();scan=ScanAsync(version,folder.Text!,extensions.Text??"",recursive.IsChecked==true,Operation,Target,scanCancellation.Token);pendingScans.Add(scan);}
        }
        finally{updating=false;States();}
    }
    private void ShowInputs(IEnumerable<string> inputs)=>table.ItemsSource=inputs.Select(path=>new BatchRowView(new(){Input=path,Operation=BatchMediaPolicy.Label(Operation),Result=BatchMediaPolicy.Supports(Operation,path,Target)?"Selected":"Unsupported input",Reason=BatchMediaPolicy.Supports(Operation,path,Target)?"Preview required":"Choose an operation/target for this file type."})).ToArray();
    private async Task ScanAsync(int generation,string path,string filter,bool recurse,BatchOperation operation,string target,CancellationToken token)
    {
        summary.Text="Scanning input folder…";
        try{await Task.Delay(250,token);var inputs=await Task.Run(()=>BatchInputSelection.Scan(path,recurse,filter,operation,target,token),token);if(closed||version!=generation||token.IsCancellationRequested)return;ShowInputs(inputs);summary.Text=$"Folder inputs: {inputs.Length}. Preview required.";}
        catch(OperationCanceledException){}catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){if(!closed&&version==generation)summary.Text="Folder scan: "+e.Message;}
    }
    private void States()
    {
        bool busy=Model.Documents.IsBusy||closeRequested;parameters.IsEnabled=!busy;clear.IsEnabled=!busy;preview.IsEnabled=!busy;execute.IsEnabled=!busy&&current&&Session?.CanExecute==true;details.IsEnabled=!busy&&current&&table.SelectedItem is BatchRowView;report.IsEnabled=!busy&&current&&Session is not null;stop.IsEnabled=Model.Documents.IsBusy;progress.IsVisible=Model.Documents.IsBusy;
    }
    internal async Task PreviewAsync()
    {
        if(Model.Documents.IsBusy)return;scanCancellation?.Cancel();int generation=++version;current=false;Session?.Invalidate();States();
        var options=new BatchOptions(Operation,output.Text??"",Target,naming.Text??"",Operation==BatchOperation.InstallBootSystem?auxiliary.Text:null,strict.IsChecked==true,replace.IsEnabled&&replace.IsChecked==true,backup.IsChecked==true,folderMode.IsChecked==true?folder.Text:null,
            Operation==BatchOperation.CompressPrograms?new(Enum.Parse<MzfCompressionAlgorithm>((string)compressionMode.SelectedItem!),direction.IsEnabled&&backward.IsChecked==true?CompressionDirection.Backward:CompressionDirection.Forward,quick.IsEnabled&&quick.IsChecked==true,embedded.IsEnabled&&embedded.IsChecked==true,0):null,
            rate.IsEnabled?(int)rate.SelectedItem!:SharpTapeExporter.WavSampleRate,heuristic.IsEnabled&&heuristic.IsChecked==true,extract.IsEnabled?(string)extract.SelectedItem!:"Native");
        bool fromFolder=folderMode.IsChecked==true,recurse=recursive.IsChecked==true;string filter=extensions.Text??"";string[] files=selected.ToArray();
        await Model.Documents.ExecuteAsync(async ct=>
        {
            if(fromFolder)files=await Task.Run(()=>BatchInputSelection.Scan(options.InputRoot??"",recurse,filter,options.Operation,options.TargetFormat,ct),ct);
            if(files.Length==0)throw new InvalidOperationException(fromFolder?"No compatible files match the folder filter and current operation/target.":"Select at least one input file.");var progress=Model.Documents.BatchProgress();var result=await Task.Run(()=>BatchProcessService.Preview(files,options,progress,ct),ct);
            if(closed||version!=generation)return false;Model.Documents.ReplaceBatch(result);current=true;table.ItemsSource=Session!.Rows;table.SelectedItem=Session.Rows.FirstOrDefault();summary.Text=(result.Cancelled?"Cancelled. Run Preview again to continue. ":"")+Session.Summary;return true;
        });States();
    }
    internal async Task ExecuteAsync()
    {
        if(!current||Session?.CanExecute!=true||!Model.BatchExecuteCommand.CanExecute(null))return;
        Model.BatchExecuteCommand.Execute(null);await Model.Documents.WaitForIdleAsync();if(Model.Documents.HasError){Session!.Invalidate();current=false;}table.ItemsSource=Session!.Rows;table.SelectedItem=Session.Rows.FirstOrDefault();summary.Text=Session.Preview.Cancelled?"Cancelled. Completed outputs are kept. Run Preview again to continue.":Session.Summary;States();
    }
    private void OpenDetails(){if(current&&table.SelectedItem is BatchRowView row)new BatchFileDetailsWindow(row.Value).Show(this);}
    private async Task ExportAsync()
    {
        if(!current||Session is not{} session)return;
        var file=await StorageProvider.SaveFilePickerAsync(new(){Title="Export TXT / CSV / JSON report",SuggestedFileName="batch.txt",ShowOverwritePrompt=false,FileTypeChoices=[new("Text"){Patterns=["*.txt"]},new("CSV"){Patterns=["*.csv"]},new("JSON"){Patterns=["*.json"]}]});
        if(file?.TryGetLocalPath() is not{} path||!current||Session!=session)return;string format=Path.GetExtension(path).TrimStart('.').ToLowerInvariant();if(format is not ("txt" or "csv" or "json")){Model.Documents.ReportError("Choose a .txt, .csv or .json report.");return;}
        await Model.Documents.ExecuteAsync(async ct=>{await Model.Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=Encoding.UTF8.GetBytes(BatchProcessService.Report(session.Preview,format))},ct);return true;});
    }
    private async Task<string?> PickFolder(string title)=>(await StorageProvider.OpenFolderPickerAsync(new(){Title=title,AllowMultiple=false})).FirstOrDefault()?.TryGetLocalPath();
    private async Task CloseSafely()
    {
        await Model.Documents.WaitForIdleAsync();await Task.WhenAll(pendingScans);foreach(var child in OwnedWindows.ToArray())child.Close();if(await Model.Documents.CloseAllAsync()){closeApproved=true;Close();}else closeRequested=false;
    }
}
