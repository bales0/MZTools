using System;
using System.IO;
using System.Windows;

namespace MZTools;

public partial class MainWindow
{
    private bool CanEditMzf=>document.Format==TapeDocumentFormat.Mzf&&mzfBlocks.Count==1&&!document.IsReadOnlyQuickDisk&&
        (document.FilePath is null||!File.Exists(document.FilePath)||!File.GetAttributes(document.FilePath).HasFlag(FileAttributes.ReadOnly));
    private void ApplyMzfEdit(TapeRecord original,TapeRecord replacement)
    {
        if(!CanEditMzf)throw new InvalidOperationException("Editing requires a writable standalone MZF.");
        int index=mzfBlocks.IndexOf(original);if(index<0)throw new InvalidOperationException("The source record changed. Reopen the editor.");
        if(TapeDocumentWriter.SerializeMzf(original,true).AsSpan().SequenceEqual(TapeDocumentWriter.SerializeMzf(replacement,true)))return;
        mzfBlocks[index]=replacement;document.IsModified=true;RefreshGrid();MzfDataGrid.SelectedIndex=index;
    }
    private void TapeProperties_Click(object sender,RoutedEventArgs e)
    {
        if(!CanEditMzf||MzfDataGrid.SelectedItems.Count!=1)return;
        int index=MzfDataGrid.SelectedIndex;if((uint)index>=mzfBlocks.Count)return;var original=mzfBlocks[index];
        new MzfDescriptionWindow(this,original,record=>{int current=mzfBlocks.IndexOf(original);ApplyMzfEdit(original,record);original=mzfBlocks[current];}).ShowDialog();
    }
}
