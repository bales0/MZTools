using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace MZTools.Desktop.Controls;

internal sealed class HexEditorCursor
{
    private readonly TextBox[] boxes;
    private readonly TextBlock status;
    private readonly Func<int,string> address;
    private bool syncing;
    private int selected;
    internal HexEditorCursor(TextBox hex,TextBox ascii,TextBox mz,TextBlock status,Func<int,string> address)
    {
        boxes=[hex,ascii,mz];this.status=status;this.address=address;
        foreach(var box in boxes)
        {
            box.IsInactiveSelectionHighlightEnabled=true;box.SelectionBrush=Brushes.DodgerBlue;
            box.PropertyChanged+=(_,e)=>
            {
                if(syncing)return;
                if(e.Property==TextBox.CaretIndexProperty)Select(box,HexPositions.CharacterIndex(box.Text??"",box.CaretIndex,box==hex));
                if(e.Property==TextBox.TextProperty)Dispatcher.UIThread.Post(()=>Select(null,selected));
            };
        }
    }
    internal void Select(TextBox? source,int index)
    {
        if(syncing)return;syncing=true;
        try
        {
            int count=HexPositions.CharacterIndex(boxes[0].Text??"",boxes[0].Text?.Length??0,true);selected=Math.Clamp(index,0,Math.Max(0,count-1));
            for(int i=0;i<boxes.Length;i++)
            {
                var box=boxes[i];int start=HexPositions.TextIndex(box.Text??"",selected,i==0);
                // Preserve the editing caret/nibble in the active field. Peers keep a visible byte selection.
                if(box==source)continue;box.SelectionStart=start;box.SelectionEnd=Math.Min(start+(i==0?2:1),box.Text?.Length??0);
            }
            status.Text=count==0?"No bytes selected":address(selected)+" · HEX / ASCII / MZASCII";
        }
        finally{syncing=false;}
    }
}
