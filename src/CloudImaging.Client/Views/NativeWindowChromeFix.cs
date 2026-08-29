using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CloudImaging.Client.Views;

/// <summary>
/// Forces removal of <em>all</em> native non-client chrome (title bar text/icon, system menu,
/// minimize/close buttons, and the classic sizing border) for <c>ui:FluentWindow</c>s that must
/// render correctly in WinPE.
/// </summary>
/// <remarks>
/// <para>
/// WPF-UI's <c>FluentWindow.OnSourceInitialized</c> unconditionally calls
/// <c>OnExtendsContentIntoTitleBarChanged</c>, which sets <see cref="Window.WindowStyle"/> back
/// to <see cref="WindowStyle.SingleBorderWindow"/>, regardless of whether
/// <c>ExtendsContentIntoTitleBar</c> is even enabled. It only hides the resulting native chrome
/// via a <see cref="System.Windows.Shell.WindowChrome"/> hack (zero-height caption) that is
/// gated on DWM composition being enabled. WinPE never runs DWM composition (no <c>dwm.exe</c>),
/// so that hack never applies and the native "Basic theme" frame stays visible around and above
/// our own <c>ui:TitleBar</c>.
/// </para>
/// <para>
/// Reasserting <see cref="Window.WindowStyle"/> = <see cref="WindowStyle.None"/> from a
/// <see cref="Window.SourceInitialized"/> handler (which does run after FluentWindow's own
/// override, since it calls <c>base.OnSourceInitialized(e)</c> last) was found to be
/// insufficient in real WinPE testing: the native chrome reappeared regardless. So this helper
/// works directly at the Win32 level instead, independent of WPF's managed window-style
/// bookkeeping and of DWM being present.
/// </para>
/// <para>
/// <b>Why the border used to survive on child windows only.</b> Stripping
/// <c>WS_CAPTION</c>/<c>WS_SYSMENU</c> alone removes the title bar but not the sizing border,
/// which is <c>WS_THICKFRAME</c>. <see cref="MainWindow"/> declares
/// <c>ResizeMode="CanMinimize"</c>, for which WPF never sets <c>WS_THICKFRAME</c> in the first
/// place, so it looked correct. Every window opened <i>from</i> it (<see cref="LogViewerWindow"/>,
/// <see cref="WifiConnectionWindow"/>) was resizable, kept <c>WS_THICKFRAME</c>, and therefore
/// kept drawing the classic raised 3D border under the non-composited Basic theme. That is the
/// border the earlier caption-only fix could never remove, and it is why the symptom tracked
/// "any window opened from the main window" rather than the main window itself.
/// </para>
/// <para>
/// <b>The fix, in three independent layers</b>, so no single one of them has to hold:
/// </para>
/// <list type="number">
/// <item><description>
/// Those child windows now declare the same <c>ResizeMode="CanMinimize"</c> as
/// <see cref="MainWindow"/>, so WPF's own bookkeeping never asks for <c>WS_THICKFRAME</c>.
/// </description></item>
/// <item><description>
/// Every border-producing style bit (<c>WS_THICKFRAME</c>, <c>WS_BORDER</c>, <c>WS_DLGFRAME</c>)
/// and 3D edge extended-style bit is cleared directly on the HWND, in case WPF-UI reasserts one
/// after WPF's managed layer has settled.
/// </description></item>
/// <item><description>
/// An HWND hook answers <c>WM_NCCALCSIZE</c> by leaving the proposed rectangle untouched, which
/// makes the client area exactly equal to the window rectangle so Windows reserves <i>zero</i>
/// non-client area and has nothing left to paint a frame into. This is the same technique
/// <c>WindowChrome</c> uses, minus the DWM gate that makes WindowChrome's version a no-op in
/// WinPE. Because the hook stays installed for the window's lifetime, a later resize,
/// activation, theme change or DPI change cannot bring the frame back the way a one-shot style
/// edit could.
/// </description></item>
/// </list>
/// </remarks>
internal static class NativeWindowChromeFix
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    private const long WS_CAPTION = 0x00C00000L; // WS_BORDER | WS_DLGFRAME
    private const long WS_BORDER = 0x00800000L;
    private const long WS_DLGFRAME = 0x00400000L;
    private const long WS_THICKFRAME = 0x00040000L; // the classic sizing border
    private const long WS_SYSMENU = 0x00080000L;

    // Raised/sunken 3D edges the Basic theme draws around the frame. None of them are wanted on
    // a window whose entire surface is our own Fluent content.
    private const long WS_EX_DLGMODALFRAME = 0x00000001L;
    private const long WS_EX_CLIENTEDGE = 0x00000200L;
    private const long WS_EX_STATICEDGE = 0x00020000L;
    private const long WS_EX_WINDOWEDGE = 0x00000100L;

    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCPAINT = 0x0085;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>
    /// Strips every piece of native non-client chrome from <paramref name="window"/>'s HWND.
    /// Must be called after the window's handle has been created (for example from a
    /// <see cref="Window.SourceInitialized"/> handler); it is a no-op before then.
    /// </summary>
    public static void RemoveNativeCaption(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Install the non-client-area hook BEFORE the SetWindowPos below, because
        // SWP_FRAMECHANGED is what triggers the WM_NCCALCSIZE that collapses the frame.
        if (HwndSource.FromHwnd(hwnd) is { } source)
        {
            source.RemoveHook(SuppressNonClientArea); // keeps this idempotent if called twice
            source.AddHook(SuppressNonClientArea);
        }

        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        long updatedStyle = style & ~(WS_CAPTION | WS_BORDER | WS_DLGFRAME | WS_THICKFRAME | WS_SYSMENU);
        if (updatedStyle != style)
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, updatedStyle);
        }

        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long updatedExStyle = exStyle & ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE);
        if (updatedExStyle != exStyle)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, updatedExStyle);
        }

        // Forces Windows to immediately recalculate the non-client area for the new styles,
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

    /// <summary>
    /// Collapses the non-client area to nothing so no caption or sizing border can be drawn.
    /// </summary>
    private static IntPtr SuppressNonClientArea(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE:
                // Returning 0 with the proposed rectangle left exactly as-is tells Windows the
                // client area fills the whole window rectangle, leaving no room for a frame.
                // Both the wParam == FALSE (RECT*) and wParam != FALSE (NCCALCSIZE_PARAMS*)
                // shapes start with the rectangle we want left untouched, so neither case needs
                // any pointer work at all.
                handled = true;
                return IntPtr.Zero;

            case WM_NCPAINT:
                // With a zero-sized non-client area there is nothing legitimate left to paint;
                // swallowing this stops the Basic theme from drawing a frame regardless.
                handled = true;
                return IntPtr.Zero;

            default:
                return IntPtr.Zero;
        }
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
