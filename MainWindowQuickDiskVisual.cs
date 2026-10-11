using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

public partial class MainWindow
{
    private QuickDiskVisualInspectorWindow? quickDiskVisual;
    private bool synchronizingQuickDiskVisual;
    private void InitializeQuickDiskVisual()
    {
        foreach(var element in new FrameworkElement[] {quickDiskMap,quickDiskBlocks,MzfDataGrid,quickDiskInspectorHost}) {
            var menu=element.ContextMenu??new ContextMenu();element.ContextMenu=menu;
            var item=new MenuItem {Header="Visual analysis…"};menu.Items.Add(item);item.Click+=(_,_)=>OpenQuickDiskVisual();
            menu.Opened+=(_,_)=>item.Visibility=document.IsQuickDisk?Visibility.Visible:Visibility.Collapsed;
        }
        Closed+=(_,_)=>quickDiskVisual?.Close();
    }
    private void QuickDiskVisual_Click(object sender,RoutedEventArgs e)=>OpenQuickDiskVisual();
    private void OpenQuickDiskVisual()
    {
        if(!document.IsQuickDisk) return;
        try {
            if(quickDiskVisual==null) {
                quickDiskVisual=new(this,QuickDiskVisualSnapshot.Capture(document),SelectQuickDiskVisualFiles);
                quickDiskVisual.Closed+=(_,_)=>quickDiskVisual=null;quickDiskVisual.Show();
            }
            SyncQuickDiskVisualSelection();quickDiskVisual.Activate();
        } catch(Exception ex) {MessageBox.Show(this,ex.Message,"QuickDisk visual analysis");}
    }
    private void RefreshQuickDiskVisual()
    {
        if(quickDiskVisual==null) return;
        if(!document.IsQuickDisk) {quickDiskVisual.Close();return;}
        try {quickDiskVisual.SetSnapshot(QuickDiskVisualSnapshot.Capture(document));SyncQuickDiskVisualSelection();}
        catch(Exception ex) {quickDiskVisual.ShowUnavailable("Cannot build current QuickDisk preview: "+ex.Message);}
    }
    private void SelectQuickDiskVisualFiles(IReadOnlyList<int> indices)
    {
        synchronizingQuickDiskVisual=true;
        try {
            MzfDataGrid.UnselectAll();
            foreach(int index in indices.Where(i=>i>=0 && i<MzfDisplayDataCollection.Count)) MzfDataGrid.SelectedItems.Add(MzfDisplayDataCollection[index]);
            quickDiskMap.HighlightFiles(indices);
        } finally {synchronizingQuickDiskVisual=false;}
    }
    private void SyncQuickDiskVisualSelection()
    {
        if(synchronizingQuickDiskVisual || quickDiskVisual==null) return;
        if(quickDiskBlocks.SelectedItem is QuickDiskRegion selected) {
            var region=quickDiskVisual.Snapshot.Layout.Regions.FirstOrDefault(r=>r.Start==selected.Start && r.Kind==selected.Kind);
            quickDiskVisual.SelectRegions(region==null?Array.Empty<QuickDiskRegion>():new[] {region},region,false);
        } else quickDiskVisual.SelectFiles(GetSelectedGridIndices());
    }
}
