using System.Runtime.InteropServices;

namespace WindowInvert.App;

internal sealed class WindowPickerOverlay : NativeWindow
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WM_PAINT = 0x000F;
    private const int WM_SETCURSOR = 0x0020;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_HOTKEY = 0x0312;
    private const int VK_ESCAPE = 0x1B;
    private const int EscapeHotKeyId = 1;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOW = 5;
    private const uint GA_ROOT = 2;
    private const int IDC_CROSS = 32515;

    /// <summary>
    /// How opaque the picking wash is, out of 255.
    /// <para>
    /// It used to be 1 - effectively invisible. Combined with a crosshair cursor
    /// that did not survive the first mouse move (see <see cref="WndProc"/>), that
    /// left a full-screen, click-swallowing window active with no indication
    /// whatsoever that the application was waiting for a click. For a user who
    /// cannot rely on subtle visual cues that is not a small defect: the next click
    /// anywhere on the desktop does something other than what it looks like it will
    /// do.
    /// </para>
    /// <para>
    /// 64 of 255 is a quarter-strength wash: unmistakable at a glance and still
    /// magnification-friendly, while leaving every window underneath clearly
    /// identifiable - which it has to be, because identifying one is the entire
    /// point of the mode. A heavier wash would flatten the contrast of the thing
    /// being chosen; a lighter one drifts back towards not being noticeable.
    /// Whether it reads well in practice is a spot-check item.
    /// </para>
    /// </summary>
    private const byte WashAlpha = 64;

    /// <summary>
    /// The wash colour, painted across the whole virtual screen. Deliberately a
    /// saturated blue rather than a grey: a desaturating wash reads as "the screen
    /// has dimmed", which happens for all sorts of reasons, whereas a colour cast
    /// reads as a mode.
    /// </summary>
    private static readonly Color WashColor = Color.FromArgb(0, 90, 200);

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

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint hCursor);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(nint hWnd, nint lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly Action<nint> _onWindowPicked;
    private readonly Action _onCancelled;

    public WindowPickerOverlay(Action<nint> onWindowPicked, Action onCancelled)
    {
        _onWindowPicked = onWindowPicked;
        _onCancelled = onCancelled;
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

        // A visible wash, not a hidden trap. Still receives clicks - there is no
        // WS_EX_TRANSPARENT here, unlike the invert overlay.
        SetLayeredWindowAttributes(Handle, 0, WashAlpha, LWA_ALPHA);

        // This window keeps its topmost placement, unlike the invert overlay and the
        // toggle button. It is a deliberate full-screen mode covering everything,
        // and something already in the topmost band - a notification toast, a media
        // OSD - would otherwise sit on top of it and take the click.
        SetWindowPos(Handle, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE);

        // Escape has to come in as a hotkey because this window can never hold
        // the keyboard focus: it is WS_EX_NOACTIVATE by design, so an ordinary
        // key message will never reach it. A hotkey is delivered regardless of
        // focus, and holding one for these few seconds is proportionate - the
        // mode is already swallowing every click on the desktop, so claiming
        // the key that conventionally means "get me out of this mode" is part
        // of the same contract, not an overreach. Right-click remains the
        // fallback if another application got to the key first.
        if (!RegisterHotKey(Handle, EscapeHotKeyId, 0, VK_ESCAPE))
        {
            Diagnostics.Log(
                $"PICKER Escape hotkey unavailable (error {Marshal.GetLastWin32Error()}); right-click still cancels");
        }
    }

    public void Show() => ShowWindow(Handle, SW_SHOW);

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_SETCURSOR:
                // Setting Cursor.Current once at construction did not survive: this
                // window handles no WM_SETCURSOR, so DefWindowProc restored the
                // class cursor on the very next mouse move and the crosshair was
                // gone before the user had moved anywhere. It has to be set from
                // here, on every message, and the message has to be reported as
                // handled or DefWindowProc undoes it again.
                SetCursor(LoadCursor(0, IDC_CROSS));
                m.Result = 1;
                return;

            case WM_PAINT:
                // The window class has no background brush, so a layered window with
                // no paint handler composes undefined content - the alpha above
                // would wash the screen with whatever happened to be in the
                // redirection surface. This is what makes the wash a colour.
                using (var g = Graphics.FromHwnd(Handle))
                {
                    g.Clear(WashColor);
                }

                // Graphics.FromHwnd wraps GetDC, not BeginPaint/EndPaint, so the
                // update region is never implicitly validated. Without this the
                // message loop keeps re-synthesizing WM_PAINT forever and pegs the
                // UI thread at 100% of a core.
                ValidateRect(Handle, 0);
                return;

            case WM_LBUTTONUP:
                GetCursorPos(out var screenPoint);
                Dismantle();

                var hit = WindowFromPoint(screenPoint);
                if (hit != 0)
                {
                    var topLevel = GetAncestor(hit, GA_ROOT);
                    _onWindowPicked(topLevel != 0 ? topLevel : hit);
                }
                else
                {
                    _onCancelled();
                }

                return;

            case WM_RBUTTONUP:
                Dismantle();
                _onCancelled();
                return;

            case WM_HOTKEY when m.WParam == EscapeHotKeyId:
                // Registered in the constructor, because Escape cannot arrive as
                // a key message - this window never takes focus.
                Dismantle();
                _onCancelled();
                return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Takes the mode down: releases the Escape hotkey, then the window. The
    /// hotkey is global state borrowed from the whole desktop, so every exit
    /// path returns it - leaking it would leave Escape dead everywhere until
    /// the process ends.
    /// </summary>
    private void Dismantle()
    {
        UnregisterHotKey(Handle, EscapeHotKeyId);
        DestroyHandle();
    }
}
