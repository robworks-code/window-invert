using System.Runtime.InteropServices;
using WindowInvert.Core.Geometry;

namespace WindowInvert.App;

internal sealed class TitleBarButtonWindow : NativeWindow
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_PAINT = 0x000F;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(nint hWnd, nint lpRect);

    private readonly Action _onClicked;

    /// <summary>
    /// Kept so the button can re-read the display scale on every reposition - a
    /// window dragged between monitors of different scales changes how wide its own
    /// caption buttons are, in the physical pixels this button is placed in.
    /// </summary>
    private readonly nint _sourceHwnd;

    private bool _isToggled;

    public TitleBarButtonWindow(nint sourceHwnd, WindowRect sourceRect, Action onClicked)
    {
        _onClicked = onClicked;
        _sourceHwnd = sourceHwnd;
        var buttonRect = OverlayGeometry.ComputeTitleBarButtonRect(
            sourceRect,
            Native.DisplayScaling.GetEffectiveDpi(sourceHwnd));

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

        // No topmost band. The button belongs to one window and should be occluded
        // whenever that window is; its z-order is asserted relative to the source
        // and the overlay by TrayApplicationContext.RestackWindow. Both windows
        // used to be topmost, and since SetWindowPos moves a window to the top of
        // that band on every call, the overlay - created later, at toggle time -
        // landed on top of the button and hid it. The user pressed the button to
        // invert and the button disappeared, taking with it the only feedback that
        // invert was on, until the window was next moved.
    }

    /// <summary>
    /// Matches the button to its source's new geometry, leaving the z-order to the
    /// separate restack pass that orders it against the overlay.
    /// </summary>
    public void Reposition(WindowRect sourceRect)
    {
        var buttonRect = OverlayGeometry.ComputeTitleBarButtonRect(
            sourceRect,
            Native.DisplayScaling.GetEffectiveDpi(_sourceHwnd));
        Native.WindowStacking.MoveTo(Handle, buttonRect.X, buttonRect.Y, buttonRect.Width, buttonRect.Height);
    }

    /// <summary>Puts this button directly below <paramref name="placeBelow"/>.</summary>
    public void InsertBelow(nint placeBelow) => Native.WindowStacking.InsertBelow(Handle, placeBelow);

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
