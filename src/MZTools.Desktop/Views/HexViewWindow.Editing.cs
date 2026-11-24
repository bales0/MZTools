using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MZTools.Desktop.Controls;

namespace MZTools.Desktop.Views;

public sealed partial class HexViewWindow
{
    private MzfHexBuffer? buffer;
    private TapeRecord? currentRecord;
    private Action<byte[]>? applyBytes;
    private Func<bool>? canEdit;
    private TableView? byteTable;
    private Button? editButton,applyButton,cancelButton,undoButton,revertButton;
    private int inputOffset,observedPayload;private ushort observedLoad;private bool lowNibble;
    internal bool IsEditing {get;private set;}
    internal bool Applied {get;private set;}
    internal byte[] EditedBytes=>buffer==null?[]:(byte[])buffer.Bytes.Clone();
    internal int SelectedOffset=>selectedRow is null?0:selectedRow.Offset+selectedByte;
    internal readonly TextBlock Status=new(){Name="HexEditStatus",Margin=new Thickness(8,0,8,8),TextWrapping=TextWrapping.Wrap};
    internal static IBrush FieldBrush(MzfHexField field)=>new SolidColorBrush(Color.FromUInt32(MzfHexPresentation.Color(field)));
    internal HexViewWindow(TapeRecord record,Action<byte[]> apply,int offset=0):this()
    {InitializeMzf(record,apply,null);BeginEdit();Opened+=(_,_)=>SelectOffset(offset);}
    private void InitializeMzf(TapeRecord record,Action<byte[]>? apply,Func<bool>? editable)
    {
        currentRecord=record;buffer=new(record);applyBytes=apply;canEdit=editable;observedPayload=MzfHexPresentation.Payload(buffer.Bytes);observedLoad=MzfHexPresentation.Load(buffer.Bytes);
        var header=new StackPanel{Spacing=5,Margin=new Thickness(8)};
        if(apply!=null)
        {
            var tools=new WrapPanel();header.Children.Add(tools);
            Button Add(string label,Action action){var b=new Button{Content=label,Margin=new Thickness(0,0,6,0)};b.Click+=(_,_)=>action();tools.Children.Add(b);return b;}
            editButton=Add("Edit",BeginEdit);undoButton=Add("Undo",Undo);revertButton=Add("Revert",Revert);applyButton=Add("Apply to document",Apply);cancelButton=Add("Cancel edits",CancelEdit);
        }
        var legend=new WrapPanel();header.Children.Add(legend);
        foreach(var field in Enum.GetValues<MzfHexField>())legend.Children.Add(new TextBlock{Text=" "+MzfHexPresentation.Label(field)+" ",Background=FieldBrush(field),Margin=new Thickness(0,0,5,0)});
        string warning=MzfDescriptionEditing.Inspect(record).Warning;
        if(warning.Length>0)header.Children.Add(new TextBlock{Text=warning,Foreground=Brushes.DarkOrange,TextWrapping=TextWrapping.Wrap});
        Content=Present(FileRows(buffer.Bytes),header);
        AddHandler(TextInputEvent,(_,e)=>{if(IsEditing){e.Handled=true;TypeHex(e.Text??"");}},RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent,async(_,e)=>
        {
            if(!IsEditing)return;var primary=OperatingSystem.IsMacOS()?KeyModifiers.Meta:KeyModifiers.Control;
            if(e.Key==Key.V&&e.KeyModifiers.HasFlag(primary))
            {
                e.Handled=true;try{if(Clipboard is {} clipboard&&await clipboard.TryGetTextAsync() is {} text)PasteHex(text);}catch(Exception ex)when(ex is not OutOfMemoryException){Status.Text=ex.Message;}
            }
            else if(e.Key==Key.Z&&e.KeyModifiers.HasFlag(primary)){e.Handled=true;Undo();}
            else if(e.Key==Key.Escape){e.Handled=true;CancelEdit();}
            else if(e.Key is Key.Delete or Key.Back or Key.Insert){e.Handled=true;}
        },RoutingStrategies.Tunnel);
        Opened+=(_,_)=>SelectOffset(0);UpdateState();
    }
    internal void BeginEdit()
    {
        if(buffer==null||applyBytes==null)return;
        if(canEdit?.Invoke()==false){Status.Text="Apply or cancel Properties drafts first; the document must remain editable.";return;}
        IsEditing=true;inputOffset=SelectedOffset;lowNibble=false;UpdateState();byteTable?.Focus();
    }
    internal void SelectOffset(int offset)
    {
        if(buffer==null)return;offset=Math.Clamp(offset,0,buffer.Bytes.Length-1);
        var row=displayed.First(r=>offset>=r.Offset&&offset<r.Offset+r.Count);SelectByte(row,offset-row.Offset);
        if(byteTable!=null){byteTable.SelectedItem=row;byteTable.Focus();}
    }
    private void UpdateState()
    {
        if(editButton!=null)editButton.IsVisible=!IsEditing;
        foreach(var b in new[]{undoButton,revertButton,applyButton,cancelButton})if(b!=null)b.IsVisible=IsEditing;
        Status.Text=IsEditing?$"Editing · {buffer?.Changes??0} changed byte(s). Type HEX or paste bytes; fixed-size overwrite. Apply changes memory only; Save writes the file.":"Read-only view · field colors and file regions are identical in edit mode.";
    }
    private void RefreshBytes()
    {
        if(buffer==null)return;int selected=SelectedOffset;
        int payload=MzfHexPresentation.Payload(buffer.Bytes);ushort load=MzfHexPresentation.Load(buffer.Bytes);
        if(payload!=observedPayload||load!=observedLoad)
        {observedPayload=payload;observedLoad=load;displayed=FileRows(buffer.Bytes);if(byteTable!=null)byteTable.ItemsSource=displayed;}
        foreach(var cell in visible.ToArray())
        {
            int index=0,column=0;
            foreach(var run in cell.Text.Inlines!.OfType<Run>())
            {
                if(cell.Representation==0&&index++%5==4)continue;
                bool present=column<cell.Row.Count;int at=cell.Row.Offset+column++;
                run.Text=cell.Representation==0?present?$"{buffer.Bytes[at]:X2} ":"   ":present?HexPositions.Character(buffer.Bytes[at],cell.Representation==2).ToString():" ";
            }
            Paint(cell.Row,cell.Representation,cell.Text);
        }
        SelectOffset(selected);UpdateState();
    }
    internal bool TypeHex(string digits)
    {
        if(!IsEditing||buffer==null||!buffer.Type(inputOffset,lowNibble,digits,out int next,out bool low))return false;
        RefreshBytes();SelectOffset(Math.Min(next,buffer.Bytes.Length-1));inputOffset=next;lowNibble=low;return true;
    }
    internal bool PasteHex(string text)
    {
        if(!IsEditing||buffer==null||!buffer.Paste(SelectedOffset,text,out int next)){Status.Text="Paste complete HEX bytes within the existing file length.";return false;}
        RefreshBytes();SelectOffset(Math.Min(next,buffer.Bytes.Length-1));inputOffset=next;return true;
    }
    internal void SetBytes(byte[] bytes){if(!IsEditing||buffer==null)throw new InvalidOperationException("Enable Edit first.");buffer.Set(bytes);RefreshBytes();}
    internal void Undo(){if(!IsEditing)return;buffer?.Undo();RefreshBytes();}
    internal void Revert(){if(!IsEditing)return;buffer?.Revert();RefreshBytes();}
    internal void CancelEdit(){if(!IsEditing)return;buffer?.Revert();buffer?.Accept();IsEditing=false;RefreshBytes();}
    internal void Apply()
    {
        if(!IsEditing||buffer==null||currentRecord==null||applyBytes==null)return;
        try
        {
            var parsed=MzfRecordEditing.ParseEdited(currentRecord,buffer.Bytes);applyBytes((byte[])buffer.Bytes.Clone());currentRecord=parsed;
            buffer.Accept();Applied=true;IsEditing=false;RefreshBytes();Status.Text="Applied to document memory. Save the document to write the file.";
        }
        catch(Exception e)when(e is InvalidDataException or InvalidOperationException or EndOfStreamException){Status.Text=e.Message;}
    }
}
