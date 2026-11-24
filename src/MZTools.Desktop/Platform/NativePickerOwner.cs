using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MZTools.Desktop.Platform;

internal static class NativePickerOwner
{
    internal static async Task<T> RunAsync<T>(TopLevel owner, Func<Task<T>> show)
    {
        try { return await show(); }
        finally { if (owner is Window window) Restore(window); }
    }

    private static void Restore(Window owner)
    {
        if (!OperatingSystem.IsWindows()) return;
        Dispatcher.UIThread.VerifyAccess();
        // The Windows picker runs on another STA. After it returns, its HWND
        // can be gone while the owner's native input is still disabled, even
        // though Avalonia and the document operation are already idle.
        // Never override a managed modal dialog or an explicitly disabled owner.
        if (!owner.IsVisible || !owner.IsEnabled || owner.OwnedWindows.Any(w => w.IsDialog)) return;
        if (owner.TryGetPlatformHandle()?.Handle is { } handle && handle != 0 && !IsWindowEnabled(handle))
            EnableWindow(handle, true);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
}
