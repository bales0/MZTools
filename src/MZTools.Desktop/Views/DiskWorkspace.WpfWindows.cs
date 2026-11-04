using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MZTools.Desktop.Views;

public sealed partial class DiskWorkspace
{
    private readonly Dictionary<int,Window> toolWindows=[];
    private bool openingTool;
    private void ConfigureWpfDiskWindows()
    {
        foreach(var tab in DiskTabs.Items.OfType<TabItem>().Skip(2))
        {tab.Width=0;tab.Height=0;tab.MinWidth=0;tab.MinHeight=0;tab.Padding=default;tab.IsHitTestVisible=false;tab.Focusable=false;}
        DiskTabs.SelectionChanged+=(_,e)=>
        {
            if(openingTool||e.Source!=DiskTabs||DiskTabs.SelectedIndex<2)return;
            int index=DiskTabs.SelectedIndex;
            Dispatcher.UIThread.Post(()=>OpenWpfDiskWindow(index));
        };
    }
    internal void OpenWpfDiskWindow(int index)
    {
        if(openingTool||index<2||index>=DiskTabs.Items.Count||TopLevel.GetTopLevel(this) is not Window owner)return;
        openingTool=true;
        try
        {
            if(observed is not null)observed.ViewIndex=0;DiskTabs.SelectedIndex=0;
            if(toolWindows.TryGetValue(index,out var existing)){existing.Activate();return;}
            if(DiskTabs.Items[index] is not TabItem tab||tab.Content is not Control content)return;
            tab.Content=null;
            var window=new Window
            {
                Title=index switch{2=>"Visual DSK Inspector — read-only",3=>"Sector Hex Editor",4=>"Physical track detail",5=>"Compare disk images",6=>"Structure Inspector",_=>"Disk report"},
                Width=1100,Height=760,MinWidth=700,MinHeight=480,DataContext=Model,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,
                Content=new Border{Padding=new Thickness(10),Child=content}
            };
            toolWindows.Add(index,window);
            window.Closed+=(_,_)=>
            {
                if(window.Content is Border border)border.Child=null;
                window.Content=null;tab.Content=content;toolWindows.Remove(index);
            };
            window.Show(owner);
        }finally{openingTool=false;}
    }
    private void CloseWpfDiskWindows()
    {foreach(var window in toolWindows.Values.ToArray())window.Close();visualInspector?.Close();}
}
