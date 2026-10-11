using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

public partial class DskEditorControl
{
    private DskVisualInspectorWindow? visualInspector;
    private void VisualAnalysis_Click(object sender,RoutedEventArgs e) => OpenVisualAnalysis("Surface");
    private void InitializeVisualCommands()
    {
        foreach(var element in new FrameworkElement[] { diskMap,mapBlocksGrid,issuesGrid,directoryGrid,multiIplGrid }) {
            var menu=element.ContextMenu??new ContextMenu(); element.ContextMenu=menu;
            if(menu.Items.Count>0) menu.Items.Add(new Separator());
            var item=new MenuItem { Header="Visual analysis…" }; item.Click+=(_,_)=>OpenVisualAnalysis("Surface"); menu.Items.Add(item);
        }
        foreach(var element in new FrameworkElement[] {diskMap,mapBlocksGrid}) {
            var item=new MenuItem {Header="Export / Import Selected Sectors…"};
            item.Click+=(_,_)=>RawRange_Click(element,new RoutedEventArgs()); element.ContextMenu!.Items.Add(item);
            element.ContextMenu.Opened+=(_,_)=>item.IsEnabled=document!=null && !IplEditorMode && selectedSectorKeys.Count>0;
        }
        Unloaded+=(_,_)=> { visualInspector?.Close(); visualInspector=null; };
    }
    private void OpenVisualAnalysis(string view)
    {
        if(document==null || diskLayout==null) { ShowError("No DSK image is available. Create or open an image first."); return; }
        if(visualInspector==null) {
            visualInspector=new DskVisualInspectorWindow(OwnerWindow,VisualSnapshot(),
                NavigateVisualSector,OpenVisualHex,SelectVisualFile,(keys,focus)=>ApplySectorSelection(keys,focus),()=>RawRange_Click(this,new RoutedEventArgs()));
            visualInspector.Closed+=(_,_)=>visualInspector=null;
            visualInspector.Show();
        }
        visualInspector.SetVerificationDetails(currentImageHash,canonicalCreatorPadding);
        SyncVisualSelection();
        if(issuesGrid.SelectedItem is DskAnalysisIssue issue && issue.Track is int track)
            visualInspector.SyncSelection(track,issue.Sector is int index?new(track,index):null);
        visualInspector.ShowView(view);
    }
    internal bool NavigateVisualSector(int track,DskSectorAddress? address)
    {
        if(diskLayout==null || !diskLayout.Tracks.Any(t=>t.PhysicalIndex==track) || address!=null && address.Track!=track) return false;
        if(address==null) {
            if(!ConfirmHexSwitch()) return false;
            if(editHexHost.Content is DskHexEditorControl pending && pending.HasPendingChanges) pending.Revert();
            ClearMapSelection(); return true;
        }
        var sector=diskLayout.Sectors.FirstOrDefault(s=>s.Address==address);
        if(sector==null) return false;
        SelectLayoutSector(sector);
        return selectedMapSector?.Address==address;
    }
    private void OpenVisualHex(DskSectorAddress address)
    {
        if(!ApplySectorSelection(selectedSectorKeys.Contains(address)?selectedSectorKeys:new[] {address}.ToHashSet(),address)) return;
        OpenWritableHex(null);
    }
    private void SelectVisualFile(string key)
    {
        if(!ConfirmHexSwitch()) return;
        if(IplEditorMode && int.TryParse(key,out int i) && i>=0 && i<multiIplRows.Count) {
            multiIplGrid.UnselectAll(); multiIplGrid.SelectedItem=multiIplRows[i]; dskViews.SelectedIndex=0;
        } else {
            var entry=directoryGrid.Items.OfType<DskFileEntry>().FirstOrDefault(f=>f.Key==key);
            if(entry==null) return;
            directoryGrid.UnselectAll(); directoryGrid.SelectedItem=entry; directoryGrid.ScrollIntoView(entry); dskViews.SelectedIndex=0;
        }
    }
    private DskVisualSnapshot VisualSnapshot()=>document!.CurrentHfe() is {} physical?new DskVisualSnapshot(physical):new DskVisualSnapshot(document.Image,diskLayout!);
    private void RefreshVisualSnapshot()
    {
        if(visualInspector==null) return;
        if(document==null || diskLayout==null) { visualInspector.Close(); return; }
        visualInspector.SetSnapshot(VisualSnapshot()); visualInspector.SetVerificationDetails(currentImageHash,canonicalCreatorPadding); SyncVisualSelection();
    }
    private void SyncVisualSelection()
    {
        if(visualInspector==null) return;
        var keys=IplEditorMode?multiIplGrid.SelectedItems.OfType<MultiGameIplRow>().Select(r=>multiIplRows.IndexOf(r).ToString()):
            directoryGrid.SelectedItems.OfType<DskFileEntry>().Select(f=>f.Key);
        visualInspector.SyncSelection(selectedMapSector?.Track??visualInspector.SelectedTrack,selectedMapSector?.Address,keys,selectedSectorKeys);
    }
}
