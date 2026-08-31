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
    /// this lock is held across the <see cref="FrameArrived"/> invocation, a handler
    /// must never call back into <see cref="Start"/>/<see cref="Stop"/>/
    /// <see cref="Dispose"/>, and must never block waiting on another thread - see
    /// <see cref="ThrowIfOnCallbackThread"/> for why, and for the guard that rejects
    /// the first case outright instead of deadlocking or corrupting state.
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
    /// Managed thread id of the thread currently inside <see cref="OnFrameArrived"/>,
    /// or 0 when no callback is running. Only ever set while <see cref="_sync"/> is
    /// held, so at most one thread owns it at a time. Volatile because
    /// <see cref="ThrowIfOnCallbackThread"/> reads it from other threads before
    /// taking any lock.
    /// </summary>
    private volatile int _callbackThreadId;

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
    /// <para>
    /// <b>A handler must not call <see cref="Start"/>, <see cref="Stop"/> or
    /// <see cref="Dispose"/>.</b> Those are rejected with an
    /// <see cref="InvalidOperationException"/> rather than being allowed to deadlock
    /// or corrupt engine state; to stop capturing in response to a frame, signal
    /// another thread and stop from there.
    /// </para>
    /// <para>
    /// <b>A handler must not synchronously block waiting on another thread</b> - a
    /// <c>Control.Invoke</c> or <c>SynchronizationContext.Send</c> onto the UI thread,
    /// for example. The engine holds its internal lock across this callback, so a
    /// handler that waits on a UI thread which is itself calling <see cref="Stop"/>
    /// deadlocks. Marshal with a post/BeginInvoke, or do the work inline.
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
        // Checked before taking any lock - acquiring _lifecycleGate from the callback
        // thread is itself one of the deadlock shapes this rejects.
        ThrowIfOnCallbackThread(nameof(Start));

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
    /// frame callback has returned, and therefore must not be called from inside a
    /// <see cref="FrameArrived"/> handler - doing so throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Stop()
    {
        // Checked before taking any lock - see ThrowIfOnCallbackThread.
        ThrowIfOnCallbackThread(nameof(Stop));

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

    /// <summary>
    /// Rejects re-entrant lifecycle calls made from inside a <see cref="FrameArrived"/>
    /// handler. Calling <see cref="Start"/>/<see cref="Stop"/>/<see cref="Dispose"/>
    /// from a handler is explicitly unsupported, and is failed fast here rather than
    /// left to corrupt state, because <see cref="System.Threading.Monitor"/>
    /// re-entrancy does <b>not</b> make it safe - it fails three separate ways:
    /// <list type="number">
    /// <item><description>Deterministic <see cref="NullReferenceException"/>: teardown
    /// nulls <c>_framePool</c> and defaults <c>_poolSize</c>, then control returns
    /// into <see cref="OnFrameArrived"/> past the frame's <c>using</c> block, where a
    /// stale <c>contentSize</c> local is compared against the defaulted
    /// <c>_poolSize</c> and <c>Recreate</c> is called on the now-null pool.</description></item>
    /// <item><description>Hard hang: re-entering <c>lock (_sync)</c> only decrements
    /// the recursion count, so the callback thread still owns <c>_sync</c> while
    /// <c>_framePool.Dispose()</c> runs - reintroducing, on this one path, exactly the
    /// dispose-under-the-callback-lock deadlock the two-lock split was written to
    /// remove.</description></item>
    /// <item><description>Lock-order inversion: the handler thread holds <c>_sync</c>
    /// and wants <c>_lifecycleGate</c>, while a UI thread in <see cref="Start"/> holds
    /// <c>_lifecycleGate</c> and waits for <c>_sync</c>.</description></item>
    /// </list>
    /// Moving the handler invocation outside <c>_sync</c> would make re-entrancy safe,
    /// but at a worse price: <c>_sync</c> is what stops <see cref="Stop"/> from
    /// disposing the shared D3D11 device while a handler is mid-render, and that race
    /// is the common path (the UI thread toggling invert off during a frame), whereas
    /// nothing in this application calls back into the engine from a handler.
    /// </summary>
    private void ThrowIfOnCallbackThread(string member)
    {
        if (_callbackThreadId != 0 && _callbackThreadId == Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                $"{nameof(CaptureEngine)}.{member} must not be called from a {nameof(FrameArrived)} handler. " +
                "Signal the intent to another thread and stop the engine from there instead.");
        }
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

            _callbackThreadId = Environment.CurrentManagedThreadId;
            try
            {
                using (var frame = _framePool.TryGetNextFrame())
                {
                    if (frame is null)
                    {
                        return;
                    }

                    contentSize = frame.ContentSize;

                    // IDirect3DSurface projects WinRT's IClosable as IDisposable, so
                    // the wrapper this property hands back is ours to release - the
                    // same inherited-member trap that hid IDirect3DDevice's
                    // IDisposable, and equally invisible in a declared-member dump.
                    using var surface = frame.Surface;

                    var texture = Direct3D11Helper.CreateD3D11Texture2DFromSurface(surface);
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
            }
            finally
            {
                _callbackThreadId = 0;
            }

            // Re-read the state rather than trusting what was captured above. The
            // guard in ThrowIfOnCallbackThread should make it impossible to arrive
            // here torn down, but a handler that *swallowed* that InvalidOperationException
            // would resume right here - so this stays defensive rather than relying on
            // an exception nobody caught.
            var pool = _framePool;
            if (!_running || pool is null || _winrtDevice is null)
            {
                return;
            }

            // The frame pool's buffers are a fixed size, so a resized window would
            // otherwise keep arriving cropped or letterboxed into the original
            // dimensions. Recreate has to happen after the frame is disposed.
            if (contentSize.Width > 0
                && contentSize.Height > 0
                && (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height))
            {
                _poolSize = contentSize;
                pool.Recreate(_winrtDevice, PixelFormat, FramePoolBufferCount, contentSize);
            }
        }
    }
}
