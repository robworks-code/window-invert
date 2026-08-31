using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace WindowInvert.Native;

/// <summary>
/// Bridges a raw Win32 <c>HWND</c> to a WinRT <see cref="GraphicsCaptureItem"/>.
/// There is no projected WinRT API for this: the only way in is the classic COM
/// interface <c>IGraphicsCaptureItemInterop</c>, exposed by the
/// <c>Windows.Graphics.Capture.GraphicsCaptureItem</c> activation factory.
/// </summary>
internal static class CaptureHelper
{
    /// <summary>
    /// <c>IID_IGraphicsCaptureItem</c> - the ABI IID of the runtime class's default
    /// interface, from the Windows SDK's <c>windows.graphics.capture.idl</c>
    /// (<c>[uuid(79C3F95B-31F7-4EC2-A464-632EF5D30760)] interface IGraphicsCaptureItem</c>).
    /// <para>
    /// This has to be a literal. The obvious-looking <c>typeof(GraphicsCaptureItem).GUID</c>
    /// is wrong under CsWinRT, and so is <c>WinRT.GuidGenerator.GetIID(...)</c> - both
    /// return the synthesized signature GUID <c>cc7b16ab-e4bc-3d0e-a4eb-4fdb9ce0a1ff</c>
    /// (note the RFC 4122 version-3 nibble), because the projected class carries no ABI
    /// <c>[Guid]</c>. Only the old .NET Framework projection made <c>typeof(...).GUID</c>
    /// yield the real IID, which is why sample code written against it still reads that
    /// way. Passing the synthesized GUID makes <c>CreateForWindow</c> return
    /// <c>E_NOINTERFACE</c>, which reaches the caller as a bare "Specified cast is not
    /// valid" <see cref="InvalidCastException"/> thrown from inside the interop stub -
    /// an error message that points nowhere near the actual cause.
    /// </para>
    /// </summary>
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// Hand-declared shim for <c>IGraphicsCaptureItemInterop</c>, declared in the
    /// Windows SDK's <c>Windows.Graphics.Capture.Interop.h</c> as:
    /// <code>
    /// DECLARE_INTERFACE_IID_(IGraphicsCaptureItemInterop, IUnknown, "3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")
    /// {
    ///     IFACEMETHOD(CreateForWindow)(HWND window, REFIID riid, void** result) PURE;
    ///     IFACEMETHOD(CreateForMonitor)(HMONITOR monitor, REFIID riid, void** result) PURE;
    /// };
    /// </code>
    /// It derives from <c>IUnknown</c> (not <c>IInspectable</c>), so the vtable
    /// layout is the three IUnknown slots followed by CreateForWindow - hence
    /// <see cref="ComInterfaceType.InterfaceIsIUnknown"/>. Declaring only
    /// CreateForWindow is safe because it is the first slot after IUnknown;
    /// CreateForMonitor is declared too so the vtable shape stays self-documenting
    /// and stays correct if a caller is ever added.
    ///
    /// Methods on a <see cref="ComImportAttribute"/> interface default to
    /// PreserveSig = false, so the declared return value maps onto the native
    /// <c>[out] void** result</c> parameter and a failing HRESULT is thrown as an
    /// exception. <c>ref Guid</c> marshals as <c>Guid*</c>, matching <c>REFIID</c>.
    /// </summary>
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);

        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    /// <summary>
    /// Creates a <see cref="GraphicsCaptureItem"/> scoped to a single top-level window.
    /// </summary>
    public static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        // GraphicsCaptureItem.As<I>() is the CsWinRT-generated static that
        // returns the runtime class's *activation factory* cast to I - which is
        // where IGraphicsCaptureItemInterop lives (it is a factory-level
        // interface, not an instance-level one).
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();

        // See GraphicsCaptureItemIid - deliberately not typeof(GraphicsCaptureItem).GUID.
        var iid = GraphicsCaptureItemIid;
        var itemPointer = interop.CreateForWindow(hwnd, ref iid);
        if (itemPointer == 0)
        {
            throw new InvalidOperationException($"IGraphicsCaptureItemInterop.CreateForWindow returned null for hwnd 0x{hwnd:X}.");
        }

        try
        {
            // FromAbi does not take ownership - it AddRefs the pointer into a new
            // projection wrapper - so the reference CreateForWindow handed us has
            // to be released here or every Start() leaks one.
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }
}
