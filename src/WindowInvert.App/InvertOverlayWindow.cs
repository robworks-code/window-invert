using System.Runtime.InteropServices;
using WindowInvert.Core.Geometry;

namespace WindowInvert.App;

internal sealed class InvertOverlayWindow : NativeWindow
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private static readonly nint HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;

    public InvertOverlayWindow(WindowRect initial)
    {
        var overlayRect = OverlayGeometry.ComputeOverlayRect(initial);

        var cp = new CreateParams
        {
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            X = overlayRect.X,
            Y = overlayRect.Y,
            Width = overlayRect.Width,
            Height = overlayRect.Height,
        };

        CreateHandle(cp);

        // Placeholder fill: semi-transparent so the underlying window is
        // still visible beneath it while proving positioning/click-through
        // ahead of the real invert pipeline (Task 11).
        SetLayeredWindowAttributes(Handle, 0, 160, LWA_ALPHA);

        // Place into the topmost z-order band immediately on creation.
        // Without this, the overlay starts as an ordinary window and only
        // gets promoted to topmost on the source window's *next* move/resize
        // (via Reposition), so activating the source window right after
        // toggle-on would restack it above the overlay in the meantime.
        SetWindowPos(Handle, HWND_TOPMOST, overlayRect.X, overlayRect.Y, overlayRect.Width, overlayRect.Height, SWP_NOACTIVATE);
    }

    public void Reposition(WindowRect sourceRect)
    {
        var overlayRect = OverlayGeometry.ComputeOverlayRect(sourceRect);
        SetWindowPos(Handle, HWND_TOPMOST, overlayRect.X, overlayRect.Y, overlayRect.Width, overlayRect.Height, SWP_NOACTIVATE);
    }

    public void Show() => ShowWindow(Handle, SW_SHOW);

    public void Hide() => ShowWindow(Handle, SW_HIDE);

    public void Destroy() => DestroyHandle();
}
