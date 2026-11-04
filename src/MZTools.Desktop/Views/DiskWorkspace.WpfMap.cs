using Avalonia.Controls;
using Avalonia.Interactivity;
using MZTools.Desktop.Platform;

namespace MZTools.Desktop.Views;

public sealed partial class DiskWorkspace
{
    private bool refreshingMap;
    private sealed record HexBufferChoice(int? Block,string Label)
    {public override string ToString()=>Label;}
    private void ConfigureWpfMap()
    {
        foreach(var table in new[]{DiskSectors,Findings})MZTools.Desktop.Controls.DskTableBehavior.Configure(table,action=>{bool before=syncing;syncing=true;try{action();}finally{syncing=before;}});
        foreach(var control in new Control[]{Map,DiskSectors,Findings,DiskFiles})
        {
            var menu=control.ContextMenu??new ContextMenu();control.ContextMenu=menu;
            var visual=new MenuItem{Header="Visual analysis…"};visual.Click+=(_,_)=>OpenWpfVisualInspector();menu.Items.Add(visual);
            if(control==Map||control==DiskSectors)
            {
                var raw=new MenuItem{Header="Export / Import Selected Sectors…"};menu.Items.Add(raw);
                foreach(string tool in new[]{"Raw export","Raw import"}){var item=new MenuItem{Header=tool=="Raw export"?"Export sectors…":"Import sectors…"};item.Click+=(_,_)=>RunMapTool(tool);raw.Items.Add(item);}
                menu.Opened+=(_,_)=>raw.IsEnabled=observed?.SectorSelection.Count>0;
            }
        }
        MapHexBuffer.SelectionChanged+=(_,_)=>{if(!refreshingMap&&MapHexBuffer.SelectedItem is HexBufferChoice choice)MapHexReason.Text=choice.Block is null?observed?.HexTitle:"Filesystem-decoded CP/M allocation block bytes.";};
        Findings.DoubleTapped+=(_,_)=>{if(observed?.SelectedFinding is not null){MapSectorDetails.Text=observed.SelectedFinding.Detail;MapInspectorTabs.SelectedIndex=1;}};
        foreach(var button in new[]{MapAllCount,MapErrorsCount,MapUnsafeCount,MapWarningsCount,MapInfoCount}){button.HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Stretch;button.VerticalAlignment=Avalonia.Layout.VerticalAlignment.Stretch;button.Margin=default;button.HorizontalContentAlignment=Avalonia.Layout.HorizontalAlignment.Center;button.VerticalContentAlignment=Avalonia.Layout.VerticalAlignment.Center;ToolTip.SetTip(button,DskIssueHelp.FilterMeaning(int.Parse((string)button.Tag!)));}
        RefreshMapOverview();
    }
    private void RefreshMapOverview()
    {
        if(refreshingMap)return;refreshingMap=true;
        try
        {
            var s=observed;var issues=s?.Snapshot.Issues??[];
            DiskAnalysisSummary.Text=s?.Layout?.Summary??s?.Summary??"Open a DSK image for read-only analysis.";
            MapAllCount.Content=$"All\n{issues.Count}";MapErrorsCount.Content=$"Errors\n{issues.Count(i=>i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe)}";
            MapUnsafeCount.Content=$"Unsafe\n{issues.Count(i=>i.Severity==DskIssueSeverity.Unsafe)}";MapWarningsCount.Content=$"Warn.\n{issues.Count(i=>i.Severity==DskIssueSeverity.Warning)}";MapInfoCount.Content=$"Info\n{issues.Count(i=>i.Severity==DskIssueSeverity.Info)}";
            DiskMapOrder.SelectedIndex=s?.LogicalOrder==true?1:0;DiskClearSelection.IsEnabled=s?.SectorSelection.Count>0;
            MapIssueFilter.SelectedIndex=s?.IssueFilter switch{"Error"=>1,"Warning"=>2,"Info"=>3,"Unsafe"=>4,_=>0};MapSeverityMeaning.Text=DskIssueHelp.FilterMeaning(MapIssueFilter.SelectedIndex);
            var selected=s?.SelectedSector;
            MapSectorDetails.Text=s?.SelectedFinding?.Detail??(selected is null?"":s?.Layout?.Sectors.FirstOrDefault(sector=>sector.Address==selected.Sector.Address)?.Detail??selected.Detail)??"";
            var previous=(MapHexBuffer.SelectedItem as HexBufferChoice)?.Block;
            var choices=new List<HexBufferChoice>();
            if(selected is not null){choices.Add(new(null,"Selected physical sector (stored bytes)"));if(s?.HasProjection==true&&s.Model.FileSystem is CpmFileSystem)choices.AddRange(selected.Sector.AllocationBlocks.Distinct().Order().Select(b=>new HexBufferChoice(b,$"CP/M allocation block {b}")));}
            MapHexBuffer.ItemsSource=choices;MapHexBuffer.SelectedItem=choices.FirstOrDefault(c=>c.Block==previous)??choices.FirstOrDefault();MapHexBuffer.IsEnabled=choices.Count>0;
            MapHexButton.IsEnabled=selected is not null;MapHexReason.Text=selected is null?"Select a sector in the map or Blocks list.":s?.CanEditHex==true?"":"Read-only decoded sector bytes.";
        }
        finally{refreshingMap=false;}
    }
    private void WpfMapOrderChanged(object? sender,SelectionChangedEventArgs e){if(!refreshingMap&&observed is not null)observed.LogicalOrder=DiskMapOrder.SelectedIndex==1;}
    private void WpfClearMap(object? sender,RoutedEventArgs e)
    {try{observed?.SetSectorSelection([],null);RestoreSelection();RefreshMapOverview();}catch(InvalidOperationException error){Model?.Documents.ReportError(error.Message);}}
    private void WpfCountFilter(object? sender,RoutedEventArgs e){if(sender is Button{Tag:string tag}&&int.TryParse(tag,out int index)){MapInspectorTabs.SelectedIndex=1;MapIssueFilter.SelectedIndex=index;}}
    private void WpfIssueFilterChanged(object? sender,SelectionChangedEventArgs e)
    {if(!refreshingMap&&observed is not null)observed.IssueFilter=MapIssueFilter.SelectedIndex switch{1=>"Error",2=>"Warning",3=>"Info",4=>"Unsafe",_=>"All"};RefreshMapOverview();}
    private void RunMapTool(string tool){if(Model is null)return;Model.DiskTool=tool;if(Model.DiskToolCommand.CanExecute(null))Model.DiskToolCommand.Execute(null);}
    private void WpfMapTool(object? sender,RoutedEventArgs e){if(sender is Button{Tag:string tool})RunMapTool(tool);}
    private void WpfOpenMapHex(object? sender,RoutedEventArgs e)
    {
        if(observed is null||observed.SelectedSector is null)return;
        try{if(MapHexBuffer.SelectedItem is HexBufferChoice{Block:int block})observed.OpenBlock(block);else observed.ViewIndex=3;}
        catch(Exception error)when(error is not OutOfMemoryException){Model?.Documents.ReportError(error.Message);}
    }
    private async void WpfSaveAnalysis(object? sender,RoutedEventArgs e)
    {
        if(Model is null||observed is null||TopLevel.GetTopLevel(this) is not Window owner)return;
        var s=observed;await Model.Documents.ExecuteAsync(async ct=>{string? path=await new DesktopDialogs(owner).PickDiskOutputAsync("Save analysis report","disk-analysis.txt");if(path is null)return false;await Model.Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=System.Text.Encoding.UTF8.GetBytes(s.Report)},ct);return true;});
    }
}
