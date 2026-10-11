using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MZTools;

public partial class DskEditorControl
{
    private readonly HashSet<DskSectorAddress> selectedSectorKeys=new();
    private DskSectorAddress? sectorAnchor;
    private IReadOnlyDictionary<string,(int Selected,int Total)> selectedOwners=new Dictionary<string,(int,int)>();
    internal IReadOnlyCollection<DskSectorAddress> SectorSelection=>selectedSectorKeys;
    private GridLength expandedDetailHeight=new(2,GridUnitType.Star);
    private void MapDetails_Expanded(object sender,RoutedEventArgs e) { if(mapDetailRow!=null) mapDetailRow.Height=expandedDetailHeight; }
    private void MapDetails_Collapsed(object sender,RoutedEventArgs e) { if(mapDetailRow==null) return; expandedDetailHeight=mapDetailRow.Height; mapDetailRow.Height=GridLength.Auto; }
    private void InitializeSectorSelection()
    {
        MapInteraction.EmphasizeSelection(multiIplGrid);
        diskMap.SelectionRequested+=(s,mods)=> {
            if(s==null) { ApplySectorSelection(Array.Empty<DskSectorAddress>(),null); return; }
            var next=DskSectorSelection.Next(selectedSectorKeys,sectorAnchor,s.Address,diskMap.OrderedAddresses,
                mods.HasFlag(ModifierKeys.Control),mods.HasFlag(ModifierKeys.Shift));
            if(ApplySectorSelection(next,next.Contains(s.Address)?s.Address:next.LastOrDefault()) && !mods.HasFlag(ModifierKeys.Shift)) sectorAnchor=s.Address;
        };
        foreach(var grid in new[] {directoryGrid,multiIplGrid}) {
            grid.Columns.Add(new DataGridTextColumn { Header="Selected sectors",IsReadOnly=true,Width=150,
                Binding=new Binding { RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(DataGridRow),1),Path=new PropertyPath("(0)",MapInteraction.LinkLabelProperty) } });
            grid.LoadingRow+=(_,e)=>UpdateOwnerRow(grid,e.Row);
        }
    }
    private void SelectLayoutSector(DskSectorLayout? sector)
    {
        if(sector==null) { ApplySectorSelection(Array.Empty<DskSectorAddress>(),null); return; }
        if(ApplySectorSelection(new[] {sector.Address},sector.Address)) sectorAnchor=sector.Address;
    }
    internal bool ApplySectorSelection(IEnumerable<DskSectorAddress> addresses,DskSectorAddress? focus)
    {
        if(diskLayout==null) return false;
        var valid=diskLayout.Sectors.ToDictionary(s=>s.Address);
        var next=addresses.Where(valid.ContainsKey).ToHashSet();
        if(focus==null || !next.Contains(focus)) focus=next.FirstOrDefault();
        if(focus==null) {
            if(!ConfirmHexSwitch()) { RefreshSectorSelection(); return false; }
            if(editHexHost.Content is DskHexEditorControl pending && pending.HasPendingChanges) pending.Revert();
            ClearMapSelection(); return true;
        }
        SelectLayoutSectorCore(valid[focus]);
        if(selectedMapSector?.Address!=focus) { RefreshSectorSelection(); return false; }
        selectedSectorKeys.Clear(); selectedSectorKeys.UnionWith(next); RefreshSectorSelection(); return true;
    }
    private void RefreshSectorSelection()
    {
        bool previous=synchronizingMapSelection; synchronizingMapSelection=true;
        try {
            diskMap.SetSelection(selectedSectorKeys,selectedMapSector);
            mapBlocksGrid.UnselectAll();
            foreach(var s in mapBlocksGrid.Items.OfType<DskSectorLayout>().Where(s=>selectedSectorKeys.Contains(s.Address))) mapBlocksGrid.SelectedItems.Add(s);
            if(selectedMapSector!=null && selectedSectorKeys.Contains(selectedMapSector.Address)) mapBlocksGrid.CurrentItem=selectedMapSector;
            selectedOwners=diskLayout==null?new Dictionary<string,(int,int)>():DskSectorSelection.Owners(diskLayout,selectedSectorKeys);
            foreach(var grid in new[] {directoryGrid,multiIplGrid}) foreach(var item in grid.Items)
                if(grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row) UpdateOwnerRow(grid,row);
            if(selectedSectorKeys.Count>1) sectorDetailText.Text=$"Selected sectors: {selectedSectorKeys.Count}\nMapped files: {selectedOwners.Count(p=>p.Value.Selected>0)}\nActive sector: {selectedMapSector?.Address}\nBlue: selected sectors. File rows show selected / total mapped sectors; Partial means only part of the file is selected.\n\n"+selectedMapSector?.Detail;
        } finally { synchronizingMapSelection=previous; }
        UpdateMapSelectionButtons(); SyncVisualSelection();
    }
    private void UpdateOwnerRow(DataGrid grid,DataGridRow row)
    {
        string? key=row.Item is DskFileEntry f?f.Key:row.Item is MultiGameIplRow m?multiIplRows.IndexOf(m).ToString():null;
        var counts=key!=null && selectedOwners.TryGetValue(key,out var c)?c:default;
        MapInteraction.SetIsRelated(row,counts.Selected>0);
        row.SetValue(MapInteraction.LinkLabelProperty,counts.Selected==0?"":$"{counts.Selected} / {counts.Total} · {(counts.Selected==counts.Total?"All":"Partial")}");
    }
}
