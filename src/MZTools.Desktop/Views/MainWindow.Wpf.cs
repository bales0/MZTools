using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

public sealed partial class MainWindow
{
    private bool wpfLayout;
    private QuickDiskVisualWindow? quickDiskInspector;
    private Button? wpfAdd,wpfExport,wpfView,wpfDelete,wpfCompression,wpfUp,wpfDown,wpfFormat;
    private MenuItem? wpfVisual;
    private readonly List<MenuItem> wpfDiskItems=[];
    private void ApplyWpfLayout()
    {
        if(wpfLayout)return;
        wpfLayout=true;
        var root=(Grid)Content!;
        root.RowDefinitions[0].Height=new GridLength(0);
        if(MainMenu.Parent is DockPanel menuParent && menuParent.Parent is Border menuBorder)menuBorder.IsVisible=false;
        if(CommandBar.Parent is Border toolbar)
        {
            toolbar.Margin=new Thickness(10,10,10,0);toolbar.Padding=new Thickness(8);
            toolbar.BorderThickness=new Thickness(1);toolbar.CornerRadius=new CornerRadius(3);
        }
        CommandBar.Children.Clear();
        var tools=new Button{Name="ToolsButton",Content="Tools ▾"};tools.Classes.Add("wpfToolbar");
        var menu=new ContextMenu();tools.ContextMenu=menu;tools.Click+=(_,_)=>menu.Open(tools);
        void Tool(string label,ICommand? command=null,EventHandler<RoutedEventArgs>? click=null)
        {var item=new MenuItem{Header=label,Command=command};if(click is not null)item.Click+=click;menu.Items.Add(item);}
        Tool("Batch Process...",click:async(_,_)=>{var batch=new AnalysisWindow(true);batch.Show(this);await batch.PrepareBatchAsync();});
        wpfVisual=new MenuItem{Header="Visual analysis…"};wpfVisual.Click+=(_,_)=>OpenWpfVisualAnalysis();menu.Items.Add(wpfVisual);
        Tool("Analyze Audio (WAV / FLAC)...",click:async(_,_)=>await OpenAudioForAnalysisAsync());
        Tool("Record Audio / Live Tape Analysis...",Model.RecorderCommand);
        Tool("MZF/M12 → COM...",click:async(_,_)=>await ConvertNativeProgramAsync());
        menu.Items.Add(new Separator());
        Tool("New tape",Model.NewTapeCommand);Tool("New QD...",Model.NewQuickDiskCommand);Tool("New disk...",Model.NewDiskCommand);
        Tool("Record properties...",Model.InspectorCommand);
        CommandBar.Children.Add(tools);
        OpenButton.Content="Open...";OpenButton.Classes.Add("wpfToolbar");CommandBar.Children.Add(OpenButton);
        Button Add(string name,string label,ICommand? command=null,EventHandler<RoutedEventArgs>? click=null)
        {var b=new Button{Name=name,Content=label,Command=command};b.Classes.Add("wpfToolbar");if(click is not null)b.Click+=click;CommandBar.Children.Add(b);return b;}
        wpfAdd=Add("AddButton","Add...",click:async(_,_)=>
        {
            if(Model.ActiveDisk is not null){Model.DiskImportCommand.Execute(null);return;}
            if(Model.ActiveTape is null)await Model.Documents.ExecuteAsync(_=>{Model.Documents.Add(TapeSession.New(false));return Task.FromResult(true);});
            if(Model.ImportCommand.CanExecute(null))Model.ImportCommand.Execute(null);
        });
        wpfExport=Add("ExportButton","Export...");wpfView=Add("ViewButton","View",click:(_,_)=>
        {if(Model.ActiveTape is not null)RecordEditor.OpenHexFromToolbar();else if(Model.ActiveDisk is{} d)d.ViewIndex=3;});
        wpfDelete=Add("DeleteButton","Delete");wpfCompression=Add("CompressionButton","Compression...",Model.CompressCommand);
        Add("SaveButton","Save",Model.SaveCommand);Add("SaveAsButton","Save As...",Model.SaveAsCommand);Add("CloseButton","Close",Model.CloseCommand);
        wpfUp=Add("MoveUpButton","↑",Model.MoveUpCommand);wpfDown=Add("MoveDownButton","↓",Model.MoveDownCommand);
        wpfUp.MinWidth=wpfDown.MinWidth=44;wpfUp.Width=wpfDown.Width=44;
        ToolTip.SetTip(wpfUp,"Move selected rows up");ToolTip.SetTip(wpfDown,"Move selected rows down");
        CommandBar.Children.Add(new Border{Width=1,Height=24,Margin=new Thickness(7,2,13,2),Background=Avalonia.Media.Brushes.LightGray});
        Add("NewQuickDiskButton","New QD...",Model.NewQuickDiskCommand);Add("NewDiskButton","New disk...",Model.NewDiskCommand);
        wpfFormat=Add("FormatQuickDiskButton","Format...",click:async(_,_)=>await FormatWpfQuickDiskAsync());
        var empty=new TableView{SelectionMode=SelectionMode.Multiple,Margin=new Thickness(0),ItemsSource=Array.Empty<object>()};
        foreach(var (header,width) in new[]{("Type",65),("Name",200),("Size",65),("Size(Hex)",85),("Trailing",75),("Start",70),("Exec",70),("Compression",100),("Description",200),("Loader",100),("Speed",65)})
            empty.Columns.Add(new TableViewColumn{Header=header,Width=new GridLength(width)});
        OverviewScroll.Content=empty;OverviewScroll.VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        if(WorkspaceTabs.Items[0] is TabItem overview)overview.Header="Files";
        ((System.Collections.Specialized.INotifyCollectionChanged)Model.Documents.Documents).CollectionChanged+=(_,_)=>UpdateWpfLayout();
        UpdateWpfLayout();
    }
    private void UpdateWpfLayout()
    {
        if(!wpfLayout)return;
        if(quickDiskInspector is not null&&quickDiskInspector.Session!=Model.ActiveTape)quickDiskInspector.Close();
        Title=Model.Documents.Active is{} doc ? "MZTools — "+doc.Title : "MZTools";
        DocumentTabs.IsVisible=Model.Documents.Documents.Count>1;
        if(DocumentTabs.Parent is Grid tabGrid && tabGrid.Parent is Border tabBorder)tabBorder.IsVisible=DocumentTabs.IsVisible;
        BodyGrid.ColumnDefinitions[0].Width=new GridLength(0);
        if(WorkspaceGrid.Parent is Grid body){body.Margin=new Thickness(10,8,10,8);body.RowSpacing=0;}
        BatchEditor.Height=AudioEditor.Height=DiskEditor.Height=RecordEditor.Height=double.NaN;
        if(CommandBar.Parent is Border wpfToolbar)wpfToolbar.IsVisible=Model.ActiveDisk is null;
        wpfAdd!.IsEnabled=Model.HasDocument&&!Model.Documents.IsBusy&&(Model.ActiveDisk is not null?Model.DiskImportCommand.CanExecute(null):Model.ActiveTape is not null&&Model.ImportCommand.CanExecute(null));
        wpfExport!.Command=Model.ActiveDisk is not null?Model.DiskExportCommand:Model.ExportCommand;
        wpfDelete!.Command=Model.ActiveDisk is not null?Model.DiskDeleteCommand:Model.DeleteRecordsCommand;
        wpfView!.IsEnabled=Model.ActiveTape?.SelectedRow is not null||Model.ActiveDisk is not null;
        wpfCompression!.IsVisible=Model.ActiveTape is not null;
        foreach(var button in CommandBar.Children.OfType<Button>().Where(b=>b.Name is "NewQuickDiskButton" or "NewDiskButton"))button.IsVisible=!Model.HasDocument;
        wpfFormat!.IsVisible=Model.ActiveTape?.IsQuickDisk==true;wpfFormat.IsEnabled=Model.ActiveTape?.CanEdit==true&&!Model.Documents.IsBusy;
        wpfUp!.IsVisible=wpfDown!.IsVisible=Model.ActiveDisk is null;
        wpfVisual!.IsVisible=Model.ActiveTape?.IsQuickDisk==true||Model.ActiveDisk is not null;
        WorkspaceTabs.IsVisible=Model.Documents.Active is not null;
        if(((Grid)Content!).Children.LastOrDefault() is Border statusBorder)statusBorder.IsVisible=Model.HasDocument||Model.Documents.IsBusy;
    }
    private void OpenWpfVisualAnalysis()
    {
        if(Model.ActiveDisk is not null){DiskEditor.OpenWpfVisualInspector();return;}
        if(Model.ActiveTape?.IsQuickDisk!=true)return;
        if(quickDiskInspector is not null){quickDiskInspector.Activate();return;}
        quickDiskInspector=new QuickDiskVisualWindow(Model);quickDiskInspector.Closed+=(_,_)=>quickDiskInspector=null;quickDiskInspector.Show(this);
    }
    private async Task FormatWpfQuickDiskAsync()
    {
        if(Model.ActiveTape is not{} tape||!tape.IsQuickDisk||!tape.CanEdit||Model.MediaDialogs is null)return;
        await Model.Documents.ExecuteAsync(async _=>
        {
            if(!await Model.MediaDialogs.ConfirmPreviewAsync("Format QuickDisk","Format the current QuickDisk? All files will be removed. Save writes the formatted image."))return false;
            tape.ApplyRecords([]);return true;
        });
    }
}
