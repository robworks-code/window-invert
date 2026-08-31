using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace WindowInvert.Native;

/// <summary>
/// Opens a <c>Windows.Graphics.Capture</c> session scoped to a single top-level
/// window and raises <see cref="FrameArrived"/> with each captured frame as a
/// D3D11 texture.
/// </summary>
public sealed class CaptureEngine : IDisposable
{
    private const int FramePoolBufferCount = 2;
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    /// <summary>
    /// Guards the capture state against the frame callback, which arrives on a
    /// thread pool thread and can therefore race <see cref="Stop"/>. Held across
    /// the whole callback (including the <see cref="FrameArrived"/> invocation) so
    /// that teardown cannot dispose the device out from under a handler that is
    /// mid-render. <see cref="Stop"/> takes the same lock; because
    /// <see cref="System.Threading.Monitor"/> is re-entrant, a handler that calls
    /// <see cref="Stop"/> on itself does not deadlock.
    /// </summary>
    private readonly object _sync = new();

    /// <summary>
    /// Serializes <see cref="Start"/> against <see cref="Stop"/> so the two cannot
    /// interleave. Deliberately a different lock from <see cref="_sync"/>: teardown
    /// disposes the frame pool while holding this one but *not* <see cref="_sync"/>,
    /// so a frame callback blocked on <see cref="_sync"/> can always make progress.
    /// The frame callback never acquires this lock, so there is no ordering
    /// inversion between the two.
    /// </summary>
    private readonly object _lifecycleGate = new();

    private ID3D11Device? _d3dDevice;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private TypedEventHandler<Direct3D11CaptureFramePool, object>? _frameArrivedHandler;
    private SizeInt32 _poolSize;
    private bool _running;

    /// <summary>
    /// Raised once per captured frame.
    /// <para>
    /// <b>Threading:</b> raised on an arbitrary thread pool thread, never on the
    /// caller's thread - the frame pool is created free-threaded so that
    /// <see cref="Start"/> does not require a <c>DispatcherQueue</c> on the calling
    /// thread. A handler that touches non-thread-safe graphics state (a D2D device
    /// context, for instance) is responsible for its own serialization. Callbacks
    /// for one engine are serialized against each other and against
    /// <see cref="Stop"/>.
    /// </para>
    /// <para>
    /// <b>Lifetime:</b> the texture is valid only for the duration of the callback
    /// and is disposed as soon as it returns. A handler must not retain it.
    /// </para>
    /// </summary>
    public event Action<ID3D11Texture2D>? FrameArrived;

    /// <summary>
    /// The D3D11 device the frames were captured on, or <see langword="null"/>
    /// before <see cref="Start"/> / after <see cref="Stop"/>.
    /// <para>
    /// Exposed so <c>InvertRenderer</c> can build its D2D device context on the
    /// exact same device that produced the captured frames -
    /// <c>CreateBitmapFromDxgiSurface</c> requires the surface and the D2D device
    /// to share one underlying D3D11 device, or it fails at runtime. A second,
    /// independently-created device is not interchangeable.
    /// </para>
    /// <para>
    /// The engine owns this device: <see cref="Stop"/> (and therefore a second
    /// <see cref="Start"/>, which stops first) disposes it, invalidating any
    /// borrowed reference. Consumers must re-read it after any restart.
    /// </para>
    /// </summary>
    public ID3D11Device? Device => _d3dDevice;

    /// <summary>
    /// Starts capturing <paramref name="hwnd"/>. Any previous capture is stopped
    /// first. Safe to call from any thread.
    /// </summary>
    public void Start(nint hwnd)
    {
        if (hwnd == 0)
        {
            throw new ArgumentException("Window handle must not be null.", nameof(hwnd));
        }

        lock (_lifecycleGate)
        {
            StopCore();

            if (!GraphicsCaptureSession.IsSupported())
            {
                throw new NotSupportedException("Windows.Graphics.Capture is not supported on this system.");
            }

            try
            {
                lock (_sync)
                {
                    // BgraSupport is required for the Direct2D interop in the render
                    // stage that consumes Device.
                    D3D11.D3D11CreateDevice(
                        null,
                        DriverType.Hardware,
                        DeviceCreationFlags.BgraSupport,
                        null,
                        out _d3dDevice).CheckError();

                    using (var dxgiDevice = _d3dDevice!.QueryInterface<IDXGIDevice>())
                    {
                        _winrtDevice = Direct3D11Helper.CreateDirect3DDeviceFromDXGIDevice(dxgiDevice);
                    }

                    _item = CaptureHelper.CreateItemForWindow(hwnd);
                    _poolSize = _item.Size;

                    // CreateFreeThreaded, not Create: Create raises FrameArrived on the
                    // calling thread and requires that thread to own a DispatcherQueue,
                    // which a WinForms UI thread does not have. Free-threaded delivery
                    // removes that requirement at the cost of the threading contract
                    // documented on FrameArrived.
                    _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                        _winrtDevice,
                        PixelFormat,
                        FramePoolBufferCount,
                        _poolSize);

                    _frameArrivedHandler = OnFrameArrived;
                    _framePool.FrameArrived += _frameArrivedHandler;

                    _session = _framePool.CreateCaptureSession(_item);
                    _running = true;
                }

                // Outside _sync: once this returns, frames start arriving on the
                // thread pool, and the callback needs _sync to do anything.
                _session!.StartCapture();
            }
            catch
            {
                StopCore();
                throw;
            }
        }
    }

    /// <summary>
    /// Stops capturing and releases every graphics resource the engine owns,
    /// including <see cref="Device"/>. Idempotent. Blocks until any in-flight
    /// frame callback has returned.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleGate)
        {
            StopCore();
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Teardown proper. Callers must already hold <see cref="_lifecycleGate"/>.
    /// </summary>
    private void StopCore()
    {
        GraphicsCaptureSession? session;
        Direct3D11CaptureFramePool? framePool;
        IDirect3DDevice? winrtDevice;
        ID3D11Device? d3dDevice;

        lock (_sync)
        {
            _running = false;

            if (_framePool is not null && _frameArrivedHandler is not null)
            {
                _framePool.FrameArrived -= _frameArrivedHandler;
            }

            _frameArrivedHandler = null;

            session = _session;
            framePool = _framePool;
            winrtDevice = _winrtDevice;
            d3dDevice = _d3dDevice;

            _session = null;
            _framePool = null;
            _item = null;
            _winrtDevice = null;
            _d3dDevice = null;
            _poolSize = default;
        }

        // Disposed after releasing _sync, deliberately. Having acquired _sync above
        // already guarantees no callback is mid-flight, and any callback that
        // arrives from here on sees _running == false and returns without touching
        // these objects - so nothing is disposed out from under a live handler.
        // Disposing the frame pool while still holding _sync would instead risk a
        // deadlock: if Close() internally waits on a pending frame delivery whose
        // handler is blocked acquiring _sync, the two would wait on each other.
        session?.Dispose();
        framePool?.Dispose();

        // IDirect3DDevice projects WinRT's IClosable as IDisposable. Skipping this
        // would leave the wrapper's reference to the underlying device alive until
        // finalization, so the D3D11 device below would not actually be destroyed
        // when Stop() returns - which would quietly weaken the "a restart
        // invalidates the previously borrowed Device" contract this type documents.
        winrtDevice?.Dispose();
        d3dDevice?.Dispose();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_sync)
        {
            if (!_running || _framePool is null)
            {
                return;
            }

            SizeInt32 contentSize;

            using (var frame = _framePool.TryGetNextFrame())
            {
                if (frame is null)
                {
                    return;
                }

                contentSize = frame.ContentSize;

                var texture = Direct3D11Helper.CreateD3D11Texture2DFromSurface(frame.Surface);
                try
                {
                    FrameArrived?.Invoke(texture);
                }
                finally
                {
                    // Disposal is idempotent, so a handler that also disposes the
                    // texture (taking the ownership the event's lifetime contract
                    // offers) is fine; this guarantees the release either way,
                    // which at capture frame rates is the difference between
                    // steady state and exhausting video memory in seconds.
                    texture.Dispose();
                }
            }

            // The frame pool's buffers are a fixed size, so a resized window would
            // otherwise keep arriving cropped or letterboxed into the original
            // dimensions. Recreate has to happen after the frame is disposed.
            if (contentSize.Width > 0
                && contentSize.Height > 0
                && (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height))
            {
                _poolSize = contentSize;
                _framePool.Recreate(_winrtDevice, PixelFormat, FramePoolBufferCount, contentSize);
            }
        }
    }
}
