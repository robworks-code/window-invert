using System.Runtime.InteropServices;

namespace WindowInvert.App;

internal sealed class WindowPickerOverlay : NativeWindow
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOW = 5;
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(System.Drawing.Point Point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly Action<nint> _onWindowPicked;

    public WindowPickerOverlay(Action<nint> onWindowPicked)
    {
        _onWindowPicked = onWindowPicked;
        var bounds = SystemInformation.VirtualScreen;

        var cp = new CreateParams
        {
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
        };

        CreateHandle(cp);

        // Fully transparent, but still receives clicks (no WS_EX_TRANSPARENT).
        SetLayeredWindowAttributes(Handle, 0, 1, LWA_ALPHA);
        Cursor.Current = Cursors.Cross;

        // Place into the topmost z-order band immediately on creation.
        // Consistent with InvertOverlayWindow/TitleBarButtonWindow: without
        // this, the overlay starts as an ordinary window and something else
        // already in the topmost band (a notification toast, a media OSD)
        // could cover part of the screen at the moment the picker is shown.
        SetWindowPos(Handle, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE);
    }

    public void Show() => ShowWindow(Handle, SW_SHOW);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_LBUTTONUP)
        {
            GetCursorPos(out var screenPoint);
            DestroyHandle();

            var hit = WindowFromPoint(screenPoint);
            if (hit != 0)
            {
                var topLevel = GetAncestor(hit, GA_ROOT);
                _onWindowPicked(topLevel != 0 ? topLevel : hit);
            }

            return;
        }

        base.WndProc(ref m);
    }
}
