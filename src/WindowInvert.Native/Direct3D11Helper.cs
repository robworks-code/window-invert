using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowInvert.Native;

/// <summary>
/// Bridges between raw D3D11/DXGI objects (Vortice) and the WinRT
/// <c>Windows.Graphics.DirectX.Direct3D11</c> projections that
/// <c>Windows.Graphics.Capture</c> speaks in. Both directions are needed:
/// D3D11 device -> <see cref="IDirect3DDevice"/> to build the frame pool, and
/// <see cref="IDirect3DSurface"/> -> <see cref="ID3D11Texture2D"/> to get at the
/// captured pixels.
/// </summary>
internal static class Direct3D11Helper
{
    /// <summary>
    /// Hand-declared shim for <c>IDirect3DDxgiInterfaceAccess</c>, declared in the
    /// Windows SDK's <c>windows.graphics.directx.direct3d11.interop.h</c> as:
    /// <code>
    /// struct __declspec(uuid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"))
    /// IDirect3DDxgiInterfaceAccess : public IUnknown
    /// {
    ///     IFACEMETHOD(GetInterface)(REFIID iid, void** p) = 0;
    /// };
    /// </code>
    /// IUnknown-derived and single-method, so GetInterface sits in the first slot
    /// after the three IUnknown slots.
    /// </summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(ref Guid iid);
    }

    /// <summary>
    /// <c>STDAPI CreateDirect3D11DeviceFromDXGIDevice(IDXGIDevice*, IInspectable**)</c>
    /// from the same header. <c>STDAPI</c> returns an HRESULT, so
    /// <c>PreserveSig = false</c> turns a failure into an exception.
    /// </summary>
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    /// <summary>
    /// Wraps a DXGI device as the WinRT <see cref="IDirect3DDevice"/> that
    /// <c>Direct3D11CaptureFramePool</c> requires.
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDeviceFromDXGIDevice(IDXGIDevice dxgiDevice)
    {
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var devicePointer);
        if (devicePointer == 0)
        {
            throw new InvalidOperationException("CreateDirect3D11DeviceFromDXGIDevice returned a null device.");
        }

        try
        {
            // IDirect3DDevice is a projected *interface*, so unlike a projected
            // runtime class it has no static FromAbi of its own - MarshalInterface<T>
            // is the CsWinRT entry point for that. Like all CsWinRT FromAbi paths it
            // AddRefs rather than taking ownership, so the out-pointer's own
            // reference is released below.
            return MarshalInterface<IDirect3DDevice>.FromAbi(devicePointer);
        }
        finally
        {
            Marshal.Release(devicePointer);
        }
    }

    /// <summary>
    /// Unwraps a captured WinRT surface back to the underlying D3D11 texture.
    /// The returned texture is owned by the caller and must be disposed;
    /// disposing it only drops this reference, it does not invalidate the frame
    /// pool's own copy.
    /// </summary>
    public static ID3D11Texture2D CreateD3D11Texture2DFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        var texturePointer = access.GetInterface(ref iid);
        if (texturePointer == 0)
        {
            throw new InvalidOperationException("IDirect3DDxgiInterfaceAccess.GetInterface returned a null ID3D11Texture2D.");
        }

        // SharpGen/Vortice's ComObject(nint) constructor *takes ownership* of the
        // reference - the opposite of the CsWinRT FromAbi convention above - so
        // there is deliberately no Marshal.Release here.
        return new ID3D11Texture2D(texturePointer);
    }
}
