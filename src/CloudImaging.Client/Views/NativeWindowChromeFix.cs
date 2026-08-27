using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CloudImaging.Client.Views;

/// <summary>
/// Forces removal of the native window caption (title bar text/icon, system menu, and
/// minimize/close buttons) at the raw Win32 level, for <c>ui:FluentWindow</c>s that must render
/// correctly in WinPE.
/// </summary>
/// <remarks>
/// <para>
/// WPF-UI's <c>FluentWindow.OnSourceInitialized</c> unconditionally calls
/// <c>OnExtendsContentIntoTitleBarChanged</c>, which sets <see cref="Window.WindowStyle"/> back
/// to <see cref="WindowStyle.SingleBorderWindow"/> — regardless of whether
/// <c>ExtendsContentIntoTitleBar</c> is even enabled. It only hides the resulting native chrome
/// via a <see cref="System.Windows.Shell.WindowChrome"/> hack (zero-height caption) that is
/// gated on DWM composition being enabled. WinPE never runs DWM composition (no <c>dwm.exe</c>),
/// so that hack never applies, and the native "Basic theme" caption (title text + system menu +
/// minimize/close buttons) stays fully visible above our own <c>ui:TitleBar</c>.
/// </para>
/// <para>
/// Simply reasserting <see cref="Window.WindowStyle"/> = <see cref="WindowStyle.None"/> from a
/// <see cref="Window.SourceInitialized"/> handler (which does run after FluentWindow's own
/// override, since it calls <c>base.OnSourceInitialized(e)</c> last) was found to be
/// insufficient in real WinPE testing — the native caption reappeared regardless. Instead, this
/// helper strips the <c>WS_CAPTION</c> / <c>WS_SYSMENU</c> style bits directly on the HWND and
/// forces Windows to immediately recompute the non-client area (<c>SWP_FRAMECHANGED</c>),
/// independent of WPF's own managed window-style bookkeeping.
/// </para>
/// </remarks>
internal static class NativeWindowChromeFix
{
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000L; // WS_BORDER | WS_DLGFRAME
    private const long WS_SYSMENU = 0x00080000L;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>
    /// Strips the native caption/system-menu chrome from <paramref name="window"/>'s HWND. Must
    /// be called after the window's handle has been created (e.g. from a
    /// <see cref="Window.SourceInitialized"/> handler) — it is a no-op before then.
    /// </summary>
    public static void RemoveNativeCaption(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        long updated = style & ~(WS_CAPTION | WS_SYSMENU);

        if (updated != style)
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, updated);
        }

        // Forces Windows to immediately recalculate the non-client area for the new style,
        // rather than waiting for the next resize/activation to notice the change.
        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private static long GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    private static void SetWindowLongPtr(IntPtr hWnd, int nIndex, long newValue)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr64(hWnd, nIndex, new IntPtr(newValue));
        }
        else
        {
            _ = SetWindowLong32(hWnd, nIndex, unchecked((int)newValue));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
