using Avalonia.Threading;
using Avalonia.Controls;

namespace MZTools.Desktop.Controls;

public sealed partial class AudioChart
{
    private readonly DispatcherTimer pcmDetailTimer=new(){Interval=TimeSpan.FromMilliseconds(50)};
    private void InitializePcmDetail()
    {
        SizeChanged+=(_,_)=>SchedulePcmDetail();
        pcmDetailTimer.Tick+=async(_,_)=>
        {
            pcmDetailTimer.Stop();
            if(Kind!=AudioPlotKind.Waveform || Session is not{} session || TopLevel.GetTopLevel(this) is null)return;
            // Enough buckets for sample-accurate detail even in a wide, high-DPI tool window.
            int buckets=Math.Clamp((int)Math.Ceiling(Math.Max(1,Plot.Width)*2),16384,65536);
            await session.RefreshViewportAsync(buckets);
        };
    }
    private void SchedulePcmDetail()
    {
        pcmDetailTimer.Stop();
        if(Kind==AudioPlotKind.Waveform && Session is not null && TopLevel.GetTopLevel(this) is not null)pcmDetailTimer.Start();
    }
}
