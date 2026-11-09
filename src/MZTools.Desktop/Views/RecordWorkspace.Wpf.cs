using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Views;

public sealed partial class RecordWorkspace
{
    private bool committingInline;
    private void ConfigureWpfRecordTabs()
    {
        if(RecordTabs.Items[2] is TabItem hex){hex.Width=0;hex.Height=0;hex.MinWidth=0;hex.MinHeight=0;hex.Padding=default;hex.IsHitTestVisible=false;hex.Focusable=false;}
        RecordTabs.SelectionChanged+=(_,e)=>
        {
            if(e.Source!=RecordTabs||RecordTabs.SelectedIndex!=2)return;
            Dispatcher.UIThread.Post(()=>{OpenWpfHex();if(observed is not null)observed.ViewIndex=0;RecordTabs.SelectedIndex=0;});
        };
    }
    internal void OpenHexFromToolbar()=>OpenWpfHex();
    private void OpenWpfHex()
    {
        if(observed?.SelectedRow is not{} row||TopLevel.GetTopLevel(this) is not Window owner)return;
        new HexViewWindow(row,observed.Model.Format==TapeDocumentFormat.Qdf).Show(owner);
    }
    private void ViewHex(object? sender,RoutedEventArgs e){OpenWpfHex();}
    private void ChangeSpeed(object? sender,SelectionChangedEventArgs e)
    {
        if(committingInline||sender is not ComboBox {DataContext:RecordRow row,SelectedItem:string value} combo||observed is null||observed.IsRefreshing||syncing)return;
        if(value==row.Speed)return;
        try{committingInline=true;observed.EditInline(row,speed:value);Model?.Documents.ClearError();}
        catch(Exception ex)when(ex is not OutOfMemoryException){Model?.Documents.ReportError(ex.Message);combo.SelectedItem=row.Speed;}
        finally{committingInline=false;}
    }
}
