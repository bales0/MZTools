using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

public sealed partial class DiskWorkspace : UserControl
{
    private ShellViewModel? Model=>DataContext as ShellViewModel;
    private DiskSession? observed;
    private bool syncing;
    private string? renamingKey;
    private Window? detail,physicalDetail;
    private PointerPressedEventArgs? press;private Point pressPoint;
    private sealed record IplReorder(DiskSession Source,long Revision,int[] Indices);
    private static readonly DataFormat<IplReorder> LocalFormat=DataFormat.CreateInProcessFormat<IplReorder>("MZTools.IplReorder");
    internal Control WorkspaceFocusRegion=>DiskTabs.SelectedIndex==0?DiskFiles:DiskTabs.SelectedIndex==3?DiskHex:DiskTabs;
    internal Control InspectorFocusRegion=>Inspector;
    public DiskWorkspace()
    {
        InitializeComponent();ConfigureFileNameCells();ConfigureWpfDiskWindows();ConfigureWpfMap();Map.MinHeight=100;DataContextChanged+=(_,_)=>BindSession();SizeChanged+=(_,_)=>Reflow();
        DetachedFromVisualTree+=(_,_)=>{if(observed is not null)observed.PropertyChanged-=Changed;observed=null;};
        AttachedToVisualTree+=(_,_)=>BindSession();
        Map.SectorSelected+=(row,mod)=>
        {
            try{observed?.SelectSector(row,mod.HasFlag(OperatingSystem.IsMacOS()?KeyModifiers.Meta:KeyModifiers.Control),mod.HasFlag(KeyModifiers.Shift));RestoreSelection();}
            catch(Exception e)when(e is InvalidOperationException){Model?.Documents.ReportError(e.Message);}
        };
        DiskTabs.SelectionChanged+=(_,_)=>Reflow();
        DiskFiles.SelectionChanged+=FileSelectionChanged;DiskSectors.SelectionChanged+=SectorSelectionChanged;
        DiskFiles.DoubleTapped+=(_,_)=>{if(TopLevel.GetTopLevel(this) is Window owner)ShowInspector(owner);};
        DiskSectors.DoubleTapped+=(_,_)=>{if(observed is not null)observed.ViewIndex=3;};
        AddHandler(KeyDownEvent,Keys,RoutingStrategies.Tunnel);
        DiskHex.AddHandler(TextInputEvent,(_,e)=>{e.Handled=true;if(observed?.HexEditingUnlocked==true&&FixedHexInput.TryOverwrite(DiskHex.Text??"",HexEditPoint,e.Text??"",out string output,out int next)){observed.HexText=output;DiskHex.CaretIndex=next;DiskHex.SelectionStart=DiskHex.SelectionEnd=next;}},RoutingStrategies.Tunnel);
        DiskHex.AddHandler(KeyDownEvent,HexKeys,RoutingStrategies.Tunnel);
        _=new HexEditorCursor(DiskHex,DiskHexAscii,DiskHexSharpAscii,DiskHexPosition,index=>observed?.HexCursorAddress(index)??"No byte buffer");
        DiskFiles.AddHandler(PointerPressedEvent,RememberPress,RoutingStrategies.Tunnel);DiskFiles.PointerMoved+=DragMove;DiskFiles.PointerReleased+=(_,_)=>press=null;
        DiskFiles.AddHandler(DragDrop.DragOverEvent,DragOver);DiskFiles.AddHandler(DragDrop.DropEvent,Drop);
    }
    internal void BindSession()
    {
        if(syncing)return;
        if(observed!=Model?.ActiveDisk)
        {
            renamingKey=null;ConfigureFileNameCells();
            CloseWpfDiskWindows();
            if(observed is not null)observed.PropertyChanged-=Changed;
            observed=Model?.ActiveDisk;if(observed is not null)observed.PropertyChanged+=Changed;
        }
        Map.Session=observed;RestoreSelection();RefreshMapOverview();Reflow();
    }
    private void Changed(object? sender,PropertyChangedEventArgs e)
    {
        Map.InvalidateVisual();
        RefreshMapOverview();
        if(e.PropertyName is nameof(DiskSession.Files) or nameof(DiskSession.Sectors) or nameof(DiskSession.SectorSelection) or nameof(DiskSession.SelectedSector))Dispatcher.UIThread.Post(RestoreSelection);
    }
    private void FileSelectionChanged(object? sender,SelectionChangedEventArgs e)
    {
        if(syncing||observed is null)return;
        if(observed.HasDraft){Dispatcher.UIThread.Post(RestoreSelection);return;}
        observed.FileSelection.Clear();observed.FileSelection.AddRange(DiskFiles.SelectedItems!.OfType<DiskFileRow>().Select(f=>f.Key));
        observed.SelectedFile=DiskFiles.SelectedItem as DiskFileRow;
        if(renamingKey is not null&&observed.SelectedFile?.Key!=renamingKey){renamingKey=null;ConfigureFileNameCells();}
        Map.InvalidateVisual();
    }
    private void SectorSelectionChanged(object? sender,SelectionChangedEventArgs e)
    {
        if(syncing||observed is null)return;
        if(observed.HasDraft){Dispatcher.UIThread.Post(RestoreSelection);return;}
        observed.SectorSelection.Clear();foreach(var row in DiskSectors.SelectedItems!.OfType<DiskSectorRow>())observed.SectorSelection.Add(row.Sector.Address);
        observed.SelectedSector=DiskSectors.SelectedItem as DiskSectorRow;Map.InvalidateVisual();
    }
    private void RestoreSelection()
    {
        if(observed is null||syncing)return;syncing=true;
        try
        {
            DiskFiles.SelectedItems!.Clear();foreach(var row in observed.Files.Where(r=>observed.FileSelection.Count!=0?observed.FileSelection.Contains(r.Key):r.Key==observed.SelectedFile?.Key))DiskFiles.SelectedItems.Add(row);
            DiskSectors.SelectedItems!.Clear();foreach(var row in observed.Sectors.Where(r=>observed.SectorSelection.Contains(r.Sector.Address)))DiskSectors.SelectedItems.Add(row);
        }
        finally{syncing=false;}
        Map.InvalidateVisual();
    }
    private void Reflow()
    {
        MinHeight=0;
        bool show=DiskTabs.SelectedIndex==0&&Bounds.Width>=900&&Model?.InspectorVisible==true;DiskInspectorPanel.IsVisible=show;
        DiskGrid.ColumnDefinitions[1].Width=new GridLength(show?16:0);DiskGrid.ColumnDefinitions[2].Width=new GridLength(show?288:0);
    }
    internal void ShowInspector(Window owner)
    {
        if(detail is not null){detail.Activate();return;}
        detail=new Window {Title="Disk inspector",Width=420,Height=720,Content=new DiskInspector {DataContext=Model,Margin=new Thickness(20)},WindowStartupLocation=WindowStartupLocation.CenterOwner};
        detail.Closed+=(_,_)=>detail=null;detail.Show(owner);
    }
    private void ZoomIn(object? sender,RoutedEventArgs e)=>Map.Zoom(1.5);
    private void ZoomOut(object? sender,RoutedEventArgs e)=>Map.Zoom(1/1.5);
    private void Fit(object? sender,RoutedEventArgs e)=>Map.Fit();
    private void FitSelection(object? sender,RoutedEventArgs e)=>Map.FitSelection();
    private void MapModeChanged(object? sender,SelectionChangedEventArgs e){if(Map is not null)Map.Mode=MapMode.SelectedIndex;}
    private void PhysicalDetail(object? sender,RoutedEventArgs e)
    {
        if(physicalDetail is not null){physicalDetail.Activate();return;}
        physicalDetail=new Window {Title="Physical track detail",Width=1000,Height=680,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new DiskPhysicalPanel {DataContext=Model,Margin=new Thickness(20)}};
        physicalDetail.Closed+=(_,_)=>physicalDetail=null;physicalDetail.Show((Window)TopLevel.GetTopLevel(this)!);
    }
    private void DifferenceSelected(object? sender,SelectionChangedEventArgs e)
    {
        if(observed is null||Differences.SelectedItem is not DiskDiffRow row)return;
        if(row.Item.LeftAddress is { } address)observed.SelectedSector=observed.Sectors.FirstOrDefault(s=>s.Sector.Address==address);
        observed.SetError(row.Detail);Map.InvalidateVisual();
    }
    private async void UnlockHex(object? sender,RoutedEventArgs e)
    {
        if(observed is not{} session||Model?.DiskDialogs is not{} dialogs)return;
        if(session.HexIsSensitive&&!await dialogs.ConfirmDiskAsync("Unlock sensitive HEX buffer","Boot/system or filesystem metadata edits can damage files or prevent booting. Unlock this fixed-size buffer?"))return;
        if(observed!=session)return;
        try{session.UnlockHexEditing();DiskHex.Focus();}catch(InvalidOperationException error){Model.Documents.ReportError(error.Message);}
    }
    private async void HexKeys(object? sender,KeyEventArgs e)
    {
        if(observed?.HexEditingUnlocked!=true)return;
        var primary=OperatingSystem.IsMacOS()?KeyModifiers.Meta:KeyModifiers.Control;
        if(e.Key is Key.Delete or Key.Space or Key.Enter or Key.Insert){e.Handled=true;return;}
        if(e.Key==Key.Back||(e.Key is Key.Left or Key.Right&&e.KeyModifiers==KeyModifiers.None))
        {DiskHex.CaretIndex=FixedHexInput.Move(DiskHex.Text??"",HexEditPoint,e.Key==Key.Right?1:-1);DiskHex.SelectionStart=DiskHex.SelectionEnd=DiskHex.CaretIndex;e.Handled=true;}
        else if(e.KeyModifiers.HasFlag(primary)&&e.Key==Key.V)
        {
            e.Handled=true;var session=observed;string text=DiskHex.Text??"";int caret=HexEditPoint;
            try
            {
                string? pasted=TopLevel.GetTopLevel(this)?.Clipboard is{} clipboard?await clipboard.TryGetTextAsync():null;
                if(session!=observed||text!=DiskHex.Text)return;
                if(pasted is not null&&FixedHexInput.TryPaste(text,caret,pasted,out string output,out int next)){session.HexText=output;DiskHex.CaretIndex=next;DiskHex.SelectionStart=DiskHex.SelectionEnd=next;}
                else Model?.Documents.ReportError("Paste rejected: complete HEX bytes must fit in the remaining buffer.");
            }catch(Exception error)when(error is not OutOfMemoryException){Model?.Documents.ReportError(error.Message);}
        }
    }
    private int HexEditPoint=>DiskHex.SelectionStart!=DiskHex.SelectionEnd?Math.Min(DiskHex.SelectionStart,DiskHex.SelectionEnd):DiskHex.CaretIndex;
    private void ConfigureFileNameCells()
    {
        DiskFiles.Columns[0].CellTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<DiskFileRow>((row,_)=>
        {
            if(row is null)return new TextBlock();
            if(row.Key!=renamingKey)return new TextBlock{Text=row.Name};
            var input=new TextBox{Text=row.Name,Name="DiskInlineRename"};
            input.AttachedToVisualTree+=(_,_)=>Dispatcher.UIThread.Post(()=>{input.Focus();input.SelectAll();});
            input.KeyDown+=async(_,e)=>
            {
                if(e.Key==Key.Escape){renamingKey=null;ConfigureFileNameCells();DiskFiles.Focus();e.Handled=true;}
                else if(e.Key==Key.Enter&&observed is{} session&&Model is{} model)
                {
                    e.Handled=true;if(session.SelectedFile?.Key!=row.Key){renamingKey=null;ConfigureFileNameCells();return;}
                    input.IsEnabled=false;bool accepted=await model.RenameDiskFileAsync(session,input.Text??"");input.IsEnabled=true;
                    if(accepted){renamingKey=null;ConfigureFileNameCells();DiskFiles.Focus();}else{input.Focus();input.SelectAll();}
                }
            };return input;
        });
    }
    private void Keys(object? sender,KeyEventArgs e)
    {
        if(Model is null||e.Source is TextBox||e.Source is Visual v&&v.GetVisualAncestors().OfType<TextBox>().Any())return;
        var primary=OperatingSystem.IsMacOS()?KeyModifiers.Meta:KeyModifiers.Control;
        if(e.Key is Key.F2 or Key.Enter&&e.Source is Visual source&&(source==DiskFiles||source.GetVisualAncestors().Contains(DiskFiles)))
        {
            if(e.Key==Key.F2){if(observed is{CanEditFileName:true,HasDraft:false}&&!Model.Documents.IsBusy){renamingKey=observed.SelectedFile!.Key;ConfigureFileNameCells();}}
            else ShowInspector((Window)TopLevel.GetTopLevel(this)!);
            e.Handled=true;return;
        }
        var command=e.Key==Key.Delete?Model.DiskDeleteCommand:e.KeyModifiers.HasFlag(primary)&&e.Key==Key.C?Model.DiskCopyCommand:e.KeyModifiers.HasFlag(primary)&&e.Key==Key.V?Model.DiskPasteCommand:null;
        if(command?.CanExecute(null)==true){command.Execute(null);e.Handled=true;}
    }
    private void RememberPress(object? sender,PointerPressedEventArgs e)
    {if(e.Source is Visual v&&(v is TableViewRow||v.GetVisualAncestors().OfType<TableViewRow>().Any())&&e.GetCurrentPoint(DiskFiles).Properties.IsLeftButtonPressed){press=e;pressPoint=e.GetPosition(DiskFiles);}}
    internal static DataTransfer CaptureDrag(DiskSession s)
    {
        var rows=DiskOperations.Selected(s);var packet=DiskOperations.CapturePacket(s);
        var transfer=new DataTransfer();transfer.Add(DataTransferItem.Create(RecordWorkspace.PacketFormat,WorkspaceDragCodec.Encode(packet)));
        if(s.Model.FileSystem is MultiGameIplFileSystem)
        {var directory=s.Model.FileSystem.ReadDirectory();transfer.Add(DataTransferItem.Create(LocalFormat,new IplReorder(s,s.Revision,rows.Select(r=>directory.ToList().FindIndex(e=>e.Key==r.Key)).ToArray())));}
        return transfer;
    }
    private async void DragMove(object? sender,PointerEventArgs e)
    {
        if(press is not { } start||observed is null||Model?.Documents.IsBusy!=false||observed.HasDraft||Math.Abs(e.GetPosition(DiskFiles).X-pressPoint.X)+Math.Abs(e.GetPosition(DiskFiles).Y-pressPoint.Y)<4)return;
        press=null;try{await DragDrop.DoDragDropAsync(start,CaptureDrag(observed),DragDropEffects.Copy|DragDropEffects.Move);}catch(Exception ex)when(ex is not OutOfMemoryException){Model.Documents.ReportError(ex.Message);}
    }
    private void DragOver(object? sender,DragEventArgs e)
    {try{e.DragEffects=Model?.CanUseDisk==true&&e.DataTransfer.Contains(RecordWorkspace.PacketFormat)?DragDropEffects.Copy:DragDropEffects.None;}catch(Exception ex)when(ex is not OutOfMemoryException){e.DragEffects=DragDropEffects.None;Model?.Documents.ReportError(ex.Message);}e.Handled=true;}
    private async void Drop(object? sender,DragEventArgs e)
    {
        e.Handled=true;if(observed is null||Model?.CanUseDisk!=true)return;var session=observed;
        await Model.Documents.ExecuteAsync(async ct=>
        {
            var local=e.DataTransfer.TryGetValue(LocalFormat);var packet=e.DataTransfer.TryGetValue(RecordWorkspace.PacketFormat);if(packet is null)return false;
            if(local?.Source==session&&!e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS()?KeyModifiers.Meta:KeyModifiers.Control))
            {
                if(session.HasDraft||session.Filter.Length!=0||session.Revision!=local.Revision)throw new InvalidOperationException("Clear the filter and edits, then start a fresh IPL drag.");
                var inputs=DiskOperations.IplInputs(session).ToList();var point=e.GetPosition(DiskFiles);
                var row=DiskFiles.GetVisualDescendants().OfType<TableViewRow>().FirstOrDefault(r=>r.TranslatePoint(default,DiskFiles) is { } p&&point.Y>=p.Y&&point.Y<p.Y+r.Bounds.Height);
                int insertion=row?.DataContext is DiskFileRow file?session.Model.FileSystem.ReadDirectory().ToList().FindIndex(f=>f.Key==file.Key)+(point.Y>row.TranslatePoint(default,DiskFiles)!.Value.Y+row.Bounds.Height/2?1:0):inputs.Count;
                ListReorder.MoveItems(inputs,local.Indices,insertion);return await Model.AcceptDiskAsync(session,DiskOperations.RebuildIpl(session,inputs),ct);
            }
            return await Model.PasteDiskAsync(session,packet,ct);
        });
    }
}
