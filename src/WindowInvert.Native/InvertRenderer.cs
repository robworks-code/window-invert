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
    private bool _disposed;

    /// <summary>
    /// Read without taking <see cref="_renderLock"/> - that lock is held across
    /// <c>Present</c>, so a UI-thread read of a diagnostic must not want it.
    /// </summary>
    private long _framesPresented;

    /// <summary>Same reasoning as <see cref="_framesPresented"/>.</summary>
    private volatile Exception? _lastRenderError;

    /// <summary>
    /// Number of frames successfully drawn and presented since the last
    /// <see cref="AttachToOverlay"/>. Diagnostic only.
    /// </summary>
    public long FramesPresented => Interlocked.Read(ref _framesPresented);

    /// <summary>
    /// The exception from the most recent failed frame, or <see langword="null"/>
    /// if the most recent frame succeeded. Render failures are swallowed rather
    /// than rethrown - see the comment in <see cref="OnFrameArrived"/> - so this
    /// is the only way to observe them.
    /// </summary>
    public Exception? LastRenderError => _lastRenderError;

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
                    // Premultiplied is the only correct value here, and the difference
                    // is not cosmetic. In this mode Direct2D unpremultiplies, applies
                    // the matrix, and re-premultiplies, which is what keeps the
                    // A' = A pass-through row producing valid output where alpha is 0.
                    // Under Straight, a fully transparent source pixel comes out
                    // FF FF FF 00 - colour greater than alpha, which is not a legal
                    // premultiplied value - and DWM composites that additively as solid
                    // white, so every rounded corner and every strip of transparent
                    // padding would render as a white block. Measured, not assumed.
                    AlphaMode = ColorMatrixAlphaMode.Premultiplied,
                };

                _compDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
                _compDevice.CreateTargetForHwnd(overlayHwnd, topmost: true, out _compTarget).CheckError();
                _compVisual = _compDevice.CreateVisual();
                _compTarget.SetRoot(_compVisual).CheckError();

                // Unlike the per-frame path, there is no last-known-good size to fall
                // back on here - the swap chain cannot be created without one.
                if (!TryGetOverlayPixelSize(overlayHwnd, out _swapChainWidth, out _swapChainHeight))
                {
                    throw new InvalidOperationException(
                        $"GetClientRect failed for overlay window 0x{overlayHwnd:X}.");
                }

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

    private void OnFrameArrived(CapturedFrame frame)
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
                using var frameSurface = frame.Texture.QueryInterface<IDXGISurface>();
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

                // A frame can legitimately arrive with no content - the engine guards
                // its own pool Recreate on exactly this - and clamping that up to 1x1
                // would magnify a single pixel across the whole overlay, painting a
                // full-surface block of whatever colour that pixel happened to be.
                // Skipping the frame leaves the previous one on screen instead.
                if (frame.ContentWidth <= 0 || frame.ContentHeight <= 0)
                {
                    return;
                }

                // The live content, never the buffer. The frame pool's buffers only
                // grow, so the texture can be bigger than the window and carry stale
                // pixels past the content edge; drawing those would both show garbage
                // and skew the scale. Clamped because a source rect outside the
                // bitmap is not drawable.
                var bufferSize = sourceBitmap.PixelSize;
                var contentWidth = Math.Clamp(frame.ContentWidth, 1, bufferSize.Width);
                var contentHeight = Math.Clamp(frame.ContentHeight, 1, bufferSize.Height);

                _invertEffect.SetInput(0, sourceBitmap, true);

                _d2dContext.Target = targetBitmap;
                _d2dContext.BeginDraw();
                drawing = true;

                // Both bitmaps are 96 DPI, so one DIP is one pixel throughout.
                //
                // The overlay is sized from the source window's DWM extended frame
                // bounds, which is the same visible region Windows.Graphics.Capture
                // composes, so for an ordinary window the two match exactly and this
                // takes the identity transform - a pixel-for-pixel copy with no
                // resampling at all. Measured on a normal resizable window: capture
                // content, extended frame bounds and overlay client area all agree,
                // while GetWindowRect is 14 px wider and 7 px taller (the invisible
                // resize border), which is what the overlay used to be sized from.
                //
                // The scaled path below is the safety net for the cases where they
                // still disagree - mid-resize, or a window whose frame DWM reports
                // differently. It scales *uniformly* and centres rather than
                // stretching: a wrong aspect ratio is worse than a letterbox, and this
                // output is read under screen magnification, where any resampling of
                // text is amplified.
                var exact = contentWidth == _swapChainWidth && contentHeight == _swapChainHeight;
                if (exact)
                {
                    _d2dContext.Transform = Matrix3x2.Identity;
                }
                else
                {
                    var scale = Math.Min(
                        _swapChainWidth / (float)contentWidth,
                        _swapChainHeight / (float)contentHeight);
                    _d2dContext.Transform =
                        Matrix3x2.CreateScale(scale)
                        * Matrix3x2.CreateTranslation(
                            (_swapChainWidth - (contentWidth * scale)) / 2f,
                            (_swapChainHeight - (contentHeight * scale)) / 2f);
                }

                _d2dContext.Clear(new Color4(0f, 0f, 0f, 0f));

                // The image rectangle crops the effect output to the live content, so
                // the pool's padding is never sampled - including by the interpolator
                // at the content edge. It is only available on the ID2D1Image
                // overload, hence going through Output rather than passing the effect
                // directly.
                //
                // Output must be disposed: Vortice's GetOutput allocates a fresh
                // wrapper over the AddRef'd native pointer on every call and caches
                // nothing, so dropping it would leak a critical-finalizable COM
                // wrapper per frame - about 60 a second per overlay - and delay the
                // native teardown after toggle-off. Vortice's own DrawImage(effect)
                // overloads dispose it in a finally for the same reason.
                using var effectOutput = _invertEffect.Output;

                _d2dContext.DrawImage(
                    effectOutput,
                    Vector2.Zero,
                    new Vortice.RawRectF(0f, 0f, contentWidth, contentHeight),
                    InterpolationMode.Linear,
                    Vortice.Direct2D1.CompositeMode.SourceOver);

                var endDraw = _d2dContext.EndDraw();
                drawing = false;
                endDraw.CheckError();

                // Sync interval 0, deliberately. This runs inside the engine's frame
                // callback, which holds the engine lock and still owns one of only two
                // frame-pool buffers, so waiting for the compositor here would pin a
                // buffer for up to a refresh interval and halve capture throughput -
                // as well as stalling a UI-thread Dispose or AttachToOverlay for that
                // long. DirectComposition paces presentation of a composition swap
                // chain itself, so interval 1 is not what prevents tearing.
                _swapChain.Present(0, PresentFlags.None).CheckError();

                Interlocked.Increment(ref _framesPresented);
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
        // A failed GetClientRect means "size unknown", not "size is 1x1". Resizing to
        // a fallback would throw away the presented content and flash the overlay for
        // a frame; keeping the last known good size is invisible and self-correcting.
        if (!TryGetOverlayPixelSize(_overlayHwnd, out var width, out var height))
        {
            return;
        }

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
    /// The overlay's client size in pixels, or <see langword="false"/> if the size
    /// could not be read. The size is clamped to at least 1x1: a swap chain may not
    /// have a zero dimension, and a minimized or zero-sized overlay legitimately
    /// reports one.
    /// </summary>
    private static bool TryGetOverlayPixelSize(nint hwnd, out uint width, out uint height)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var rect))
        {
            width = 0;
            height = 0;
            return false;
        }

        width = (uint)Math.Max(1, rect.Right - rect.Left);
        height = (uint)Math.Max(1, rect.Bottom - rect.Top);
        return true;
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
        Interlocked.Exchange(ref _framesPresented, 0);
        _lastRenderError = null;
    }
}
