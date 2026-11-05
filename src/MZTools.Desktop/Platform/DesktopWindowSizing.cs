using Avalonia.Controls;

namespace MZTools.Desktop.Platform;

internal static class DesktopWindowSizing
{
    internal static void FitToWorkingArea(Window window)
    {
        window.Opened+=(_,_)=>{
            var screen=window.Screens.ScreenFromWindow(window)??window.Screens.Primary;if(screen is null)return;
            double width=Math.Max(1,screen.WorkingArea.Width/screen.Scaling-24),height=Math.Max(1,screen.WorkingArea.Height/screen.Scaling-24);
            window.MinWidth=Math.Min(window.MinWidth,width);window.MinHeight=Math.Min(window.MinHeight,height);
            window.Width=Math.Min(window.Width,width);window.Height=Math.Min(window.Height,height);
        };
    }
}
