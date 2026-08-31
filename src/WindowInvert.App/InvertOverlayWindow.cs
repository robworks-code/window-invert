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
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private readonly Native.CaptureEngine _captureEngine = new();
    private readonly Native.InvertRenderer _renderer = new();
    private readonly Action<Exception>? _onPipelineFailed;
    private int _failureReported;

    /// <param name="onPipelineFailed">
    /// Called the first time capture or rendering fails for this overlay.
    /// <para>
    /// <b>Raised on a thread pool thread, holding the capture engine's callback
    /// lock.</b> The handler must not tear this overlay down inline: doing so calls
    /// <c>CaptureEngine.Stop</c>/<c>Dispose</c> from inside the engine's own frame
    /// callback, which the engine rejects with an <c>InvalidOperationException</c>
    /// rather than deadlocking. Post the teardown to the UI thread.
    /// </para>
    /// </param>
    public InvertOverlayWindow(WindowRect initial, nint sourceHwnd, Action<Exception>? onPipelineFailed = null)
    {
        _onPipelineFailed = onPipelineFailed;
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

        // Deliberately NOT placed in the topmost band, and never given
        // WS_EX_TOPMOST. This window shows a live copy of one other window; putting
        // it in the topmost band made it float over every unrelated window on the
        // screen, and because it is click-through the user could end up reading one
        // window while typing into the one underneath. Its z-order is instead
        // asserted relative to its own source by the owner of both handles - see
        // TrayApplicationContext.RestackWindow - which is also what puts the toggle
        // button back on top of it.
        //
        // The window is created without WS_EX_TOPMOST, so there is no stale style
        // to clear; and a window inserted below a non-topmost window loses topmost
        // status anyway, so the restack itself is self-correcting.

        try
        {
            // Subscribed before anything can fail, so a failure during startup is
            // reported the same way as one an hour later.
            _captureEngine.CaptureFailed += OnPipelineFailed;
            _renderer.RenderFailed += OnPipelineFailed;

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
            _captureEngine.CaptureFailed -= OnPipelineFailed;
            _renderer.RenderFailed -= OnPipelineFailed;
            SafeDispose(_captureEngine, nameof(_captureEngine));
            SafeDispose(_renderer, nameof(_renderer));
            DestroyHandle();
            throw;
        }
    }

    /// <summary>
    /// Reports the first capture or render failure and ignores the rest.
    /// <para>
    /// Runs on a thread pool thread; see the constructor parameter's contract for
    /// why the handler may not act on this inline. Reported once because the render
    /// path keeps producing failures - once a graphics device is lost, every
    /// subsequent frame fails the same way - and the consumer only needs telling
    /// that this overlay has stopped working.
    /// </para>
    /// </summary>
    private void OnPipelineFailed(Exception error)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0)
        {
            return;
        }

        _onPipelineFailed?.Invoke(error);
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

    /// <summary>
    /// Matches the overlay to its source's new geometry. Leaves the z-order alone -
    /// restacking is a separate pass with a defined order between the overlay and
    /// the toggle button, and doing it here would fight that.
    /// </summary>
    public void Reposition(WindowRect sourceRect)
    {
        var overlayRect = OverlayGeometry.ComputeOverlayRect(sourceRect);
        Native.WindowStacking.MoveTo(Handle, overlayRect.X, overlayRect.Y, overlayRect.Width, overlayRect.Height);
    }

    /// <summary>Puts this overlay directly below <paramref name="placeBelow"/>.</summary>
    public void InsertBelow(nint placeBelow) => Native.WindowStacking.InsertBelow(Handle, placeBelow);

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
        //
        // Unsubscribed first so a failure raised during teardown cannot re-enter the
        // consumer, which is very likely already in the middle of destroying this
        // overlay because of an earlier failure.
        _captureEngine.CaptureFailed -= OnPipelineFailed;
        _renderer.RenderFailed -= OnPipelineFailed;

        _captureEngine.Dispose();
        _renderer.Dispose();
        DestroyHandle();
    }
}
