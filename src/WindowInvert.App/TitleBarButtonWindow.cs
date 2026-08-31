using System.Runtime.InteropServices;
using WindowInvert.Core.Geometry;

namespace WindowInvert.App;

internal sealed class TitleBarButtonWindow : NativeWindow
{
    private const int ButtonSize = 20;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_PAINT = 0x000F;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(nint hWnd, nint lpRect);

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly Action _onClicked;
    private bool _isToggled;

    public TitleBarButtonWindow(WindowRect sourceRect, Action onClicked)
    {
        _onClicked = onClicked;
        var buttonRect = OverlayGeometry.ComputeTitleBarButtonRect(sourceRect, ButtonSize);

        var cp = new CreateParams
        {
            Style = WS_POPUP,
            ExStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            X = buttonRect.X,
            Y = buttonRect.Y,
            Width = buttonRect.Width,
            Height = buttonRect.Height,
        };

        CreateHandle(cp);

        // Place into the topmost z-order band immediately on creation.
        // Without this, the button starts as an ordinary window and only
        // gets promoted to topmost on the source window's *next* move/resize
        // (via Reposition), so activating the source window right after
        // creation would restack it above the button in the meantime.
        SetWindowPos(Handle, HWND_TOPMOST, buttonRect.X, buttonRect.Y, buttonRect.Width, buttonRect.Height, SWP_NOACTIVATE);
    }

    public void Reposition(WindowRect sourceRect)
    {
        var buttonRect = OverlayGeometry.ComputeTitleBarButtonRect(sourceRect, ButtonSize);
        SetWindowPos(Handle, HWND_TOPMOST, buttonRect.X, buttonRect.Y, buttonRect.Width, buttonRect.Height, SWP_NOACTIVATE);
    }

    public void SetToggledVisual(bool isToggled)
    {
        _isToggled = isToggled;
        InvalidateRect(Handle, 0, true);
    }

    public void Show() => ShowWindow(Handle, SW_SHOW);

    public void Hide() => ShowWindow(Handle, SW_HIDE);

    public void Destroy() => DestroyHandle();

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_LBUTTONUP:
                _onClicked();
                return;

            case WM_PAINT:
                using (var g = Graphics.FromHwnd(Handle))
                {
                    g.Clear(_isToggled ? Color.OrangeRed : Color.DimGray);
                }
                // Graphics.FromHwnd wraps GetDC, not BeginPaint/EndPaint, so the
                // update region is never implicitly validated. Without this call
                // the message loop keeps re-synthesizing WM_PAINT forever, pegging
                // the UI thread at 100% of a core from the moment a button is shown.
                ValidateRect(Handle, 0);
                return;
        }

        base.WndProc(ref m);
    }
}
