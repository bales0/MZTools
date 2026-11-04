using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace MZTools.Desktop.Views;

public sealed partial class AudioWorkspace
{
    private void ConfigureWpfAudioLayout()
    {
        AudioGrid.MinHeight=0;
        // The waveform stays visible while long analysis/detail panes scroll independently.
        foreach(var tab in AudioTabs.Items.OfType<TabItem>())
        {
            if(tab.Content is not Control content)continue;
            int minimum=tab.Header?.ToString() switch {"Pulses"=>540,"Recovery"=>650,"Spectral"=>560,_=>0};
            if(minimum==0)continue;
            content.MinHeight=minimum;tab.Content=null;
            tab.Content=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        }
    }
}
