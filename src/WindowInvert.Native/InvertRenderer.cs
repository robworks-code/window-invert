using System.Diagnostics;
using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using WindowInvert.Native.Interop;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace WindowInvert.Native;

/// <summary>
/// Presents the frames a <see cref="CaptureEngine"/> produces into an overlay
/// window, colour-inverted, using Direct2D for the effect graph and
/// DirectComposition for presentation.
/// <para>
/// The effect graph is deliberately a single <see cref="ColorMatrix"/>: it is the
/// seam a future smart-invert pass slots into, replacing only the effect below
/// without touching capture or window tracking around it.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="AttachToOverlay"/> runs on the caller's thread
/// (the UI thread, in this application) while <see cref="OnFrameArrived"/> runs
/// on an arbitrary thread pool thread - see the threading contract on
/// <see cref="CaptureEngine.FrameArrived"/>. That split is safe because neither
/// Direct2D nor DirectComposition objects are thread-*affine*; both only require
/// that concurrent access be serialized ("DirectComposition objects are not
/// thread bound", and Direct2D's multithreading guidance is about concurrency,
/// never about which thread created a resource). Serialization here comes from
/// two places: the engine serializes its own callbacks against each other, and
/// <see cref="_renderLock"/> serializes those callbacks against
/// <see cref="AttachToOverlay"/>/<see cref="Dispose"/> on the UI thread.
/// Note that thread affinity would not have been achievable anyway - the engine
/// raises frames on arbitrary pool threads, so deferring setup to the first
/// callback would still leave every later frame on a different thread.
/// </para>
/// </summary>
public sealed class InvertRenderer : IDisposable
{
    /// <summary>
    /// Negates RGB and passes alpha through unchanged.
    /// <para>
    /// Direct2D's colour matrix is 5 rows by 4 columns; the first four rows scale
    /// the R, G, B and A inputs and the fifth row is a constant offset, so an
    /// output channel is <c>out = R*m1c + G*m2c + B*m3c + A*m4c + m5c</c>. With
    /// the values below that is <c>R' = -R + 1</c>, <c>G' = -G + 1</c>,
    /// <c>B' = -B + 1</c>, <c>A' = A</c>.
    /// </para>
    /// <para>
    /// Alpha is passed through rather than forced opaque so that a source window's
    /// transparent regions (Windows 11 rounded corners, for instance) stay
    /// transparent in the overlay instead of being painted as inverted black.
    /// </para>
    /// </summary>
    private static readonly Matrix5x4 InvertMatrix = new(
        -1, 0, 0, 0,
        0, -1, 0, 0,
        0, 0, -1, 0,
        0, 0, 0, 1,
        1, 1, 1, 0);

    private const Format SwapChainFormat = Format.B8G8R8A8_UNorm;

    /// <summary>
    /// Serializes the frame callback against <see cref="AttachToOverlay"/> and
    /// <see cref="Dispose"/>, which are called from the UI thread. Without it, a
    /// toggle-off during a frame would dispose the D2D context out from under a
    /// handler that is mid-draw. Held only by this type; the frame callback
    /// acquires it while holding the engine's own lock, and the UI thread
    /// acquires it while holding no engine lock, so the two cannot invert.
    /// </summary>
    private readonly object _renderLock = new();

    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _d2dContext;
    private ColorMatrix? _invertEffect;
    private IDCompositionDevice? _compDevice;
    private IDCompositionTarget? _compTarget;
    private IDCompositionVisual? _compVisual;
    private IDXGISwapChain1? _swapChain;
    private CaptureEngine? _engine;
    private nint _overlayHwnd;
    private uint _swapChainWidth;
    private uint _swapChainHeight;
    private long _framesPresented;
    private Exception? _lastRenderError;
    private bool _disposed;

    /// <summary>
    /// Number of frames successfully drawn and presented since the last
    /// <see cref="AttachToOverlay"/>. Diagnostic only.
    /// </summary>
    public long FramesPresented
    {
        get { lock (_renderLock) { return _framesPresented; } }
    }

    /// <summary>
    /// The exception from the most recent failed frame, or <see langword="null"/>
    /// if the most recent frame succeeded. Render failures are swallowed rather
    /// than rethrown - see the comment in <see cref="OnFrameArrived"/> - so this
    /// is the only way to observe them.
    /// </summary>
    public Exception? LastRenderError
    {
        get { lock (_renderLock) { return _lastRenderError; } }
    }

    /// <summary>
    /// Binds a DirectComposition visual tree to <paramref name="overlayHwnd"/> and
    /// starts drawing <paramref name="engine"/>'s frames into it. Any previous
    /// attachment is torn down first. <paramref name="engine"/> must already be
    /// started, because its D3D11 device is the one everything here is built on.
    /// </summary>
    public void AttachToOverlay(nint overlayHwnd, CaptureEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        if (overlayHwnd == 0)
        {
            throw new ArgumentException("Overlay window handle must not be null.", nameof(overlayHwnd));
        }

        lock (_renderLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReleaseResources();

            // Must be the exact device CaptureEngine captured the frames on:
            // CreateBitmapFromDxgiSurface requires the surface and the D2D device
            // to share one underlying D3D11 device. A second, independently
            // created device fails at runtime. Deliberately borrowed into a local
            // and never stored - the engine owns it and disposes it on Stop(), so
            // a field here would go stale on the next toggle.
            var d3dDevice = engine.Device ?? throw new InvalidOperationException(
                "CaptureEngine must be started (engine.Start(hwnd)) before AttachToOverlay.");

            _overlayHwnd = overlayHwnd;

            try
            {
                using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();

                // MultiThreaded: only one thread ever drives this context, but the
                // underlying D3D11 device is genuinely shared with the capture
                // pipeline, so letting D2D take its own lock costs one enum value
                // and removes a whole class of question.
                _d2dDevice = D2D1.D2D1CreateDevice(dxgiDevice, new CreationProperties
                {
                    ThreadingMode = ThreadingMode.MultiThreaded,
                    DebugLevel = DebugLevel.None,
                    Options = DeviceContextOptions.None,
                });

                _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
                _invertEffect = new ColorMatrix(_d2dContext)
                {
                    Matrix = InvertMatrix,
                    // Captured frames and the swap chain are both premultiplied, and
                    // for the opaque pixels that dominate a window that is identical
                    // to straight alpha. Stated explicitly rather than left to the
                    // effect's default so the choice is visible next to the matrix.
                    AlphaMode = ColorMatrixAlphaMode.Premultiplied,
                };

                _compDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
                _compDevice.CreateTargetForHwnd(overlayHwnd, topmost: true, out _compTarget).CheckError();
                _compVisual = _compDevice.CreateVisual();
                _compTarget.SetRoot(_compVisual).CheckError();

                (_swapChainWidth, _swapChainHeight) = GetOverlayPixelSize(overlayHwnd);

                // IDXGIDevice::GetParent returns the ADAPTER, not the factory - the
                // factory is the adapter's parent, one level further up.
                using var adapter = dxgiDevice.GetAdapter();
                using var dxgiFactory = adapter.GetParent<IDXGIFactory2>();

                _swapChain = dxgiFactory.CreateSwapChainForComposition(d3dDevice, new SwapChainDescription1
                {
                    // A composition swap chain has no HWND to infer a size from, so
                    // unlike CreateSwapChainForHwnd these must be non-zero.
                    Width = _swapChainWidth,
                    Height = _swapChainHeight,
                    Format = SwapChainFormat,
                    Stereo = false,
                    // Omitting this leaves BufferUsage at 0, which DXGI rejects.
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    // CreateSwapChainForComposition documents both of these as
                    // required values, not preferences.
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipSequential,
                    // Premultiplied so the source window's transparent regions stay
                    // transparent once DirectComposition composes the overlay.
                    AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
                    SampleDescription = new SampleDescription(1, 0),
                    Flags = SwapChainFlags.None,
                });

                _compVisual.SetContent(_swapChain).CheckError();
                _compDevice.Commit().CheckError();

                _engine = engine;

                // Deliberately the last statement. Everything above is published to
                // the callback thread by this subscription (and by the engine's own
                // lock around the invocation); subscribing earlier would let a frame
                // observe a half-built renderer.
                engine.FrameArrived += OnFrameArrived;
            }
            catch
            {
                ReleaseResources();
                throw;
            }
        }
    }

    private void OnFrameArrived(ID3D11Texture2D frame)
    {
        lock (_renderLock)
        {
            if (_disposed || _swapChain is null || _d2dContext is null || _invertEffect is null)
            {
                return;
            }

            var drawing = false;

            try
            {
                EnsureSwapChainSize();

                // The texture belongs to the engine and is disposed as soon as this
                // returns, so nothing here may outlive the callback - hence the
                // SetInput(0, null) in the finally below, which is what actually
                // stops the effect from pinning it.
                using var frameSurface = frame.QueryInterface<IDXGISurface>();
                using var sourceBitmap = _d2dContext.CreateBitmapFromDxgiSurface(
                    frameSurface,
                    new BitmapProperties1(
                        new PixelFormat(SwapChainFormat, AlphaMode.Premultiplied),
                        96f,
                        96f,
                        BitmapOptions.None));

                using var backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
                using var targetBitmap = _d2dContext.CreateBitmapFromDxgiSurface(
                    backBuffer,
                    new BitmapProperties1(
                        new PixelFormat(SwapChainFormat, AlphaMode.Premultiplied),
                        96f,
                        96f,
                        BitmapOptions.Target | BitmapOptions.CannotDraw));

                var sourceSize = sourceBitmap.PixelSize;
                if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
                {
                    return;
                }

                _invertEffect.SetInput(0, sourceBitmap, true);

                _d2dContext.Target = targetBitmap;
                _d2dContext.BeginDraw();
                drawing = true;

                // Both bitmaps are created at 96 DPI, so one DIP is one pixel and
                // this scale maps the captured window onto the overlay's client
                // area even when the two differ by a pixel or two.
                _d2dContext.Transform = Matrix3x2.CreateScale(
                    _swapChainWidth / (float)sourceSize.Width,
                    _swapChainHeight / (float)sourceSize.Height);
                _d2dContext.Clear(new Color4(0f, 0f, 0f, 0f));
                _d2dContext.DrawImage(_invertEffect);

                var endDraw = _d2dContext.EndDraw();
                drawing = false;
                endDraw.CheckError();

                _swapChain.Present(1, PresentFlags.None).CheckError();

                _framesPresented++;
                _lastRenderError = null;
            }
            catch (Exception ex)
            {
                // Swallowed on purpose. This runs on a thread pool thread, so an
                // escaping exception terminates the process - a transient
                // device-removed or a failed Present would take the whole tray app
                // down with it, and for a user who depends on this overlay for
                // legibility a stalled overlay is strictly better than a crash. The
                // failure is recorded rather than lost; the next frame retries.
                _lastRenderError = ex;
                Debug.WriteLine($"{nameof(InvertRenderer)}: frame dropped - {ex}");

                if (drawing)
                {
                    // Leaving the context inside BeginDraw would make every
                    // subsequent frame fail too.
                    try
                    {
                        _d2dContext.EndDraw();
                    }
                    catch (Exception endDrawEx)
                    {
                        Debug.WriteLine($"{nameof(InvertRenderer)}: EndDraw after failure - {endDrawEx}");
                    }
                }
            }
            finally
            {
                // Release the effect's reference to this frame's bitmap and the
                // context's reference to the back buffer. Without the first, the
                // effect keeps the captured texture alive across frames and starves
                // the engine's two-buffer frame pool; without the second,
                // ResizeBuffers fails the first time the overlay is resized.
                ClearFrameReferences();
            }
        }
    }

    /// <summary>
    /// Matches the swap chain to the overlay's current client size. Callers must
    /// hold <see cref="_renderLock"/>.
    /// </summary>
    private void EnsureSwapChainSize()
    {
        var (width, height) = GetOverlayPixelSize(_overlayHwnd);

        if (width == _swapChainWidth && height == _swapChainHeight)
        {
            return;
        }

        ClearFrameReferences();
        _swapChain!.ResizeBuffers(0, width, height, Format.Unknown, SwapChainFlags.None).CheckError();
        _swapChainWidth = width;
        _swapChainHeight = height;
    }

    /// <summary>
    /// Drops the two references a frame takes that would otherwise outlive it.
    /// Callers must hold <see cref="_renderLock"/>.
    /// </summary>
    private void ClearFrameReferences()
    {
        try
        {
            _invertEffect?.SetInput(0, null, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{nameof(InvertRenderer)}: clearing effect input - {ex}");
        }

        try
        {
            if (_d2dContext is not null)
            {
                _d2dContext.Target = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{nameof(InvertRenderer)}: clearing render target - {ex}");
        }
    }

    /// <summary>
    /// The overlay's client size in pixels, clamped to at least 1x1 because a
    /// swap chain may not have a zero dimension and a minimized or zero-sized
    /// overlay legitimately reports one.
    /// </summary>
    private static (uint Width, uint Height) GetOverlayPixelSize(nint hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var rect))
        {
            return (1, 1);
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        return ((uint)Math.Max(1, width), (uint)Math.Max(1, height));
    }

    public void Dispose()
    {
        lock (_renderLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseResources();
        }
    }

    /// <summary>
    /// Releases everything this type owns, leaving it re-attachable. Callers must
    /// hold <see cref="_renderLock"/>, which is what guarantees no frame callback
    /// is mid-draw while this runs.
    /// </summary>
    private void ReleaseResources()
    {
        if (_engine is not null)
        {
            _engine.FrameArrived -= OnFrameArrived;
            _engine = null;
        }

        ClearFrameReferences();

        // Released leaf-first so each native object's last reference drops here
        // rather than at some later finalization: the target holds the visual, the
        // visual holds the swap chain, and the effect holds the context.
        _compTarget?.Dispose();
        _compVisual?.Dispose();
        _compDevice?.Dispose();
        _swapChain?.Dispose();
        _invertEffect?.Dispose();
        _d2dContext?.Dispose();
        _d2dDevice?.Dispose();

        // The D3D11 device is deliberately absent from that list. CaptureEngine
        // created it and disposes it in Stop()/Dispose(); this type only ever
        // borrowed it, and never held a field for it.

        _compTarget = null;
        _compVisual = null;
        _compDevice = null;
        _swapChain = null;
        _invertEffect = null;
        _d2dContext = null;
        _d2dDevice = null;

        _overlayHwnd = 0;
        _swapChainWidth = 0;
        _swapChainHeight = 0;
        _framesPresented = 0;
        _lastRenderError = null;
    }
}
