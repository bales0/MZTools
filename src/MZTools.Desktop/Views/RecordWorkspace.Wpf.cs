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
    private void RenameInline(object? sender,RoutedEventArgs e)=>FocusInlineName();
    private void ViewHex(object? sender,RoutedEventArgs e){OpenWpfHex();}
    private void CommitName(object? sender,RoutedEventArgs e)
    {
        if(committingInline||sender is not TextBox {DataContext:RecordRow row} text||observed is null||text.Text==row.Name)return;
        try{committingInline=true;observed.EditInline(row,newName:text.Text??"");Model?.Documents.ClearError();}
        catch(Exception ex)when(ex is not OutOfMemoryException){Model?.Documents.ReportError(ex.Message);text.Text=row.Name;}
        finally{committingInline=false;}
    }
    private void NameKey(object? sender,KeyEventArgs e)
    {
        if(sender is not TextBox {DataContext:RecordRow row} text)return;
        if(e.Key==Key.Escape){text.Text=row.Name;RecordsTable.Focus();e.Handled=true;}
        else if(e.Key==Key.Enter){CommitName(sender,e);RecordsTable.Focus();e.Handled=true;}
    }
    private void ChangeLoader(object? sender,SelectionChangedEventArgs e)=>ChangeProfile(sender,true);
    private void ChangeSpeed(object? sender,SelectionChangedEventArgs e)=>ChangeProfile(sender,false);
    private void ChangeProfile(object? sender,bool loader)
    {
        if(committingInline||sender is not ComboBox {DataContext:RecordRow row,SelectedItem:string value} combo||observed is null||syncing)return;
        if(value==(loader?row.Loader:row.Speed))return;
        try{committingInline=true;observed.EditInline(row,loader:loader?value:null,speed:loader?null:value);Model?.Documents.ClearError();}
        catch(Exception ex)when(ex is not OutOfMemoryException){Model?.Documents.ReportError(ex.Message);combo.SelectedItem=loader?row.Loader:row.Speed;}
        finally{committingInline=false;}
    }
    private void FocusInlineName()
    {
        var text=RecordsTable.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t=>t.DataContext==observed?.SelectedRow);
        if(text is not null){text.Focus();text.SelectAll();}
    }
}
