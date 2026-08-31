using System.Diagnostics;
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

    private readonly Native.CaptureEngine _captureEngine = new();
    private readonly Native.InvertRenderer _renderer = new();

    public InvertOverlayWindow(WindowRect initial, nint sourceHwnd)
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

        // The placeholder's semi-transparency is gone - DirectComposition now
        // supplies every pixel - but the call itself is kept, at a no-op alpha of
        // 255. A layered window "will not become visible until
        // SetLayeredWindowAttributes or UpdateLayeredWindow has been called for
        // this window"; measured behaviour is that the DirectComposition layer is
        // composed regardless, but that is not something the documentation
        // promises, and the whole feature depends on this window being displayed.
        // One call keeps it inside the documented guarantee.
        //
        // WS_EX_LAYERED itself stays because click-through depends on it:
        // WS_EX_TRANSPARENT's hit-test pass-through is documented in terms of a
        // layered window, and swapping in WS_EX_NOREDIRECTIONBITMAP - the usual
        // style for a DirectComposition-only window - measurably breaks it
        // (WindowFromPoint then returns the overlay instead of the window
        // underneath). DirectComposition explicitly supports a layered target.
        SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);

        // Place into the topmost z-order band immediately on creation.
        // Without this, the overlay starts as an ordinary window and only
        // gets promoted to topmost on the source window's *next* move/resize
        // (via Reposition), so activating the source window right after
        // toggle-on would restack it above the overlay in the meantime.
        SetWindowPos(Handle, HWND_TOPMOST, overlayRect.X, overlayRect.Y, overlayRect.Width, overlayRect.Height, SWP_NOACTIVATE);

        try
        {
            // Start capturing before attaching: the renderer builds its Direct2D
            // device on the engine's D3D11 device, which only exists once the engine
            // is running.
            _captureEngine.Start(sourceHwnd);
            _renderer.AttachToOverlay(Handle, _captureEngine);
        }
        catch
        {
            // Destroy() can never run for an instance whose constructor threw - it
            // never reaches the caller, let alone TrayApplicationContext's overlay
            // map - so everything acquired above has to be released here. Leaving it
            // would strand a live Windows.Graphics.Capture session with no owner,
            // which on Windows 11 means the yellow capture border sits on the user's
            // window for the rest of the process's life with no way to clear it, plus
            // an orphaned topmost click-through HWND that nothing will ever destroy.
            // Same order as Destroy(): stop producing, then tear down the consumer.
            //
            // Each release is guarded separately, because a cleanup path that can
            // itself fail is not a cleanup path. Closing a WinRT capture session or
            // frame pool can throw an RPC/COM error, and an escape from the first
            // Dispose would skip DestroyHandle and leave behind exactly the orphaned
            // topmost window this block exists to prevent - while also replacing the
            // real failure with a less informative one. The original exception is the
            // one that propagates.
            SafeDispose(_captureEngine, nameof(_captureEngine));
            SafeDispose(_renderer, nameof(_renderer));
            DestroyHandle();
            throw;
        }
    }

    /// <summary>
    /// Releases <paramref name="disposable"/> without letting a failure there
    /// escape. Only for constructor rollback, where an escaping exception would
    /// skip the rest of the cleanup and mask the real failure.
    /// </summary>
    private static void SafeDispose(IDisposable disposable, string what)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{nameof(InvertOverlayWindow)}: disposing {what} during rollback failed: {ex}");
        }
    }

    public void Reposition(WindowRect sourceRect)
    {
        var overlayRect = OverlayGeometry.ComputeOverlayRect(sourceRect);
        SetWindowPos(Handle, HWND_TOPMOST, overlayRect.X, overlayRect.Y, overlayRect.Width, overlayRect.Height, SWP_NOACTIVATE);
    }

    public void Show() => ShowWindow(Handle, SW_SHOW);

    public void Hide() => ShowWindow(Handle, SW_HIDE);

    public void Destroy()
    {
        // Capture first: CaptureEngine.Stop() blocks until any in-flight frame
        // handler has returned, so the renderer is provably idle before its GPU
        // resources go away. InvertRenderer also guards itself with its own lock,
        // so the reverse order would be safe too - this is simply the order that
        // never contends. The renderer must go before DestroyHandle, since its
        // DirectComposition target is bound to this HWND.
        _captureEngine.Dispose();
        _renderer.Dispose();
        DestroyHandle();
    }
}
