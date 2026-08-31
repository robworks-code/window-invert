using System.Runtime.InteropServices;

namespace WindowInvert.Native.Interop;

internal static class NativeMethods
{
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    public delegate void WinEventProc(
        nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    /// <summary>
    /// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>. Asks DWM for the window's <i>visible</i>
    /// frame, excluding the invisible resize border that
    /// <see cref="GetWindowRect"/> includes on Windows 10 and later.
    /// </summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>
    /// <c>DWMWA_CLOAKED</c>. Non-zero when DWM is not compositing the window even
    /// though it is still <c>WS_VISIBLE</c> - a suspended store app, or a window
    /// sitting on a virtual desktop the user has switched away from. The particular
    /// non-zero value says who cloaked it (the app, the shell, or an owner), which
    /// this app has no reason to distinguish.
    /// </summary>
    public const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// Returns an HRESULT rather than a BOOL, so callers must compare against
    /// <c>S_OK</c> (0) rather than treating any value as success.
    /// </summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hWnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    /// <summary>
    /// The <c>DWORD</c>-valued form, for attributes like
    /// <see cref="DWMWA_CLOAKED"/>. Same HRESULT contract as the
    /// <see cref="RECT"/> form above.
    /// <para>
    /// Deliberately a separate managed name rather than an overload: both forms
    /// differ only in an <c>out</c> parameter, which <c>out var</c> at the call site
    /// cannot disambiguate.
    /// </para>
    /// </summary>
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeInt(nint hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>
    /// Returns the number of characters copied, or 0 on failure. The name a
    /// window class was registered under is the only thing that distinguishes
    /// the desktop, the taskbar and the magnifier from an application window -
    /// see <c>NonApplicationWindowClasses</c>.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(nint hWnd, int nIndex);

    /// <summary>
    /// The DPI a window is being rendered at, per its own DPI awareness. Only a
    /// fallback here - see <c>DisplayScaling</c> for why the monitor's effective
    /// DPI is the right number for measuring another process's caption buttons.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    /// <summary>Returns an HRESULT, so callers must compare against <c>S_OK</c> (0).</summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Note <c>hWndInsertAfter</c>: it is the window that must <i>precede</i>
    /// <paramref name="hWnd"/> in the z-order, so <paramref name="hWnd"/> ends up
    /// <b>below</b> it. Measured, because the name reads the other way round.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(System.Drawing.Point Point);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    public const uint GA_ROOT = 2;

    /// <summary>
    /// <c>GA_ROOTOWNER</c>. Walks both the parent and the owner chain to the top, so
    /// a modal dialog resolves to the application window that owns it in one call.
    /// That is exactly the window Windows raises alongside the dialog when the
    /// dialog is activated, and therefore the other window whose overlay and toggle
    /// button need putting back.
    /// </summary>
    public const uint GA_ROOTOWNER = 3;

    /// <summary>The window directly <i>above</i> the given one in the z-order.</summary>
    public const uint GW_HWNDPREV = 3;

    public const uint GW_OWNER = 4;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;

    /// <summary>
    /// <c>HWND_TOPMOST</c>. Windows keeps every topmost window above every
    /// non-topmost one, so this is band membership, not merely a position.
    /// </summary>
    public static readonly nint HWND_TOPMOST = new(-1);

    /// <summary><c>HWND_NOTOPMOST</c>: leave the topmost band, clearing the style.</summary>
    public static readonly nint HWND_NOTOPMOST = new(-2);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary><c>MDT_EFFECTIVE_DPI</c>: the monitor's DPI including the user's scale setting.</summary>
    public const int MDT_EFFECTIVE_DPI = 0;
    public const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017;
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;

    /// <summary>
    /// <c>OBJID_WINDOW</c>. The accessible object that <i>is</i> the window, as
    /// opposed to a child element inside it. It says nothing about whether the
    /// window is top-level - a tooltip, a menu popup and a child window all raise
    /// events with this object id - so it is a necessary filter, not a sufficient
    /// one.
    /// </summary>
    public const int OBJID_WINDOW = 0;

    /// <summary><c>CHILDID_SELF</c>: the object itself rather than an element of it.</summary>
    public const int CHILDID_SELF = 0;
}
