using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowInvert.App;

/// <summary>
/// A context menu that stays open until the user actually dismisses it, and
/// that does not treat the Alt key as an instruction to close.
/// <para>
/// Both behaviours exist for the same reason. Working through a magnified
/// viewport means crossing the screen in sections to reach the menu and then
/// reading down it, so opening the menu and choosing something from it is
/// measured in seconds, not in the fraction of a second Windows assumes. A menu
/// that can vanish partway through is not merely irritating, it is unusable -
/// the reported symptom was never getting as far as selecting anything at all.
/// </para>
/// </summary>
internal sealed class MagnifiableContextMenuStrip : ContextMenuStrip
{
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_SYSCHAR = 0x0106;

    /// <summary><c>VK_MENU</c> - the Alt key itself.</summary>
    private const int VK_MENU = 0x12;

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    /// <summary>
    /// Held in a field for the lifetime of the menu. A delegate passed straight
    /// into <c>SetWindowsHookEx</c> is not rooted by the unmanaged side, so a
    /// local one is collected at an unpredictable later moment and the next
    /// mouse event calls into freed memory - a crash minutes away from its
    /// cause.
    /// </summary>
    private readonly LowLevelMouseProc _mouseProc;

    private nint _mouseHook;

    public MagnifiableContextMenuStrip()
    {
        _mouseProc = HandleLowLevelMouse;

        // The whole point. Left to itself WinForms closes this menu whenever
        // another application takes the foreground, and on a real desktop that
        // happens constantly for reasons that have nothing to do with the user:
        // the trace that led here showed one background program creating a new
        // top-level window every 1.2 seconds, forever, each one taking the
        // foreground and killing the menu three milliseconds later.
        //
        // Nothing on screen explains that, so from the user's side the menu just
        // disappears at random. Turning AutoClose off means the menu is
        // dismissed only by the things below - choosing an item, clicking
        // outside it, or Escape - which is also, for a magnification user, the
        // behaviour a menu should have had in the first place.
        AutoClose = false;

        ItemClicked += HandleItemClicked;
    }

    /// <summary>
    /// Extends the same policy to submenus, which are separate drop-downs with
    /// their own <see cref="ToolStripDropDown.AutoClose"/>.
    /// <para>
    /// Without this the Windows list - the part of this menu anyone actually
    /// browses, and the part that takes longest to read - would still be closing
    /// on every foreground change while the menu around it stayed put.
    /// </para>
    /// </summary>
    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);

        if (e.Item is not ToolStripMenuItem item)
        {
            return;
        }

        item.DropDown.AutoClose = false;
        item.DropDown.ItemClicked += HandleItemClicked;
        item.MouseEnter += HandleItemMouseEnter;
    }

    /// <summary>
    /// Opens the hovered item's submenu and closes any other, which is what
    /// Windows does for a menu in its own menu mode.
    /// <para>
    /// Done explicitly because that mode is exactly what turning
    /// <see cref="ToolStripDropDown.AutoClose"/> off opts out of. Hover-expand
    /// is not a flourish here: reaching a submenu by clicking its parent first
    /// is an extra precise mouse action, and precise mouse actions are the cost
    /// this menu is trying to remove.
    /// </para>
    /// </summary>
    private void HandleItemMouseEnter(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem entered)
        {
            return;
        }

        foreach (var other in Items.OfType<ToolStripMenuItem>())
        {
            if (!ReferenceEquals(other, entered) && other.HasDropDownItems && other.DropDown.Visible)
            {
                other.HideDropDown();
            }
        }

        if (entered.HasDropDownItems && !entered.DropDown.Visible)
        {
            entered.ShowDropDown();
        }
    }

    /// <summary>
    /// Closes the menu once the user has chosen something. With
    /// <see cref="ToolStripDropDown.AutoClose"/> off this no longer happens by
    /// itself.
    /// <para>
    /// A parent item is not a choice - clicking "Windows" opens its list - so
    /// only items with nothing underneath them dismiss the menu.
    /// </para>
    /// </summary>
    private void HandleItemClicked(object? sender, ToolStripItemClickedEventArgs e)
    {
        if (e.ClickedItem is ToolStripMenuItem { HasDropDownItems: true })
        {
            return;
        }

        Close(ToolStripDropDownCloseReason.ItemClicked);
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        base.OnOpening(e);

        if (e.Cancel)
        {
            return;
        }

        InstallMouseHook();
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        RemoveMouseHook();

        // Closing the root does not reliably take a submenu with it once that
        // submenu has AutoClose off, and a stranded Windows list floating alone
        // over the desktop is the failure this whole class exists to avoid.
        foreach (var item in Items.OfType<ToolStripMenuItem>())
        {
            if (item.HasDropDownItems && item.DropDown.Visible)
            {
                item.HideDropDown();
            }
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// Starts watching for a click outside the menu, which is the dismissal this
    /// menu no longer gets for free.
    /// <para>
    /// A low-level hook rather than a timer polling the mouse: a poll cannot see
    /// a click shorter than its own interval, and "I clicked away and it stayed
    /// open" is verbatim a bug this menu has already produced once. The hook is
    /// installed only while the menu is up, and its callback does one rectangle
    /// test.
    /// </para>
    /// <para>
    /// If it cannot be installed the menu falls back to the stock behaviour for
    /// that showing. A menu with no dismissal logic at all is the worse failure
    /// of the two: an undismissable menu covers the screen and outlasts every
    /// attempt to get rid of it, where the stock behaviour is merely the bug
    /// this is fixing.
    /// </para>
    /// </summary>
    private void InstallMouseHook()
    {
        if (_mouseHook != 0)
        {
            return;
        }

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, 0, 0);

        if (_mouseHook == 0)
        {
            AutoClose = true;
            Diagnostics.Log(
                $"MENU mouse hook install failed (error {Marshal.GetLastWin32Error()}); "
                + "falling back to AutoClose");
            return;
        }

        AutoClose = false;
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == 0)
        {
            // Only ever true after a failed install, which turned AutoClose on.
            AutoClose = false;
            return;
        }

        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = 0;
    }

    /// <summary>
    /// Dismisses the menu when a mouse button goes down anywhere that is not
    /// part of it.
    /// <para>
    /// Runs on the UI thread, since low-level mouse events are delivered to the
    /// thread that installed the hook. The close is posted rather than performed
    /// here: this is the middle of Windows' own input dispatch, and tearing the
    /// menu down inside it means re-entering the message pump from a hook
    /// callback that has not returned yet.
    /// </para>
    /// </summary>
    private nint HandleLowLevelMouse(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && IsButtonDown((int)wParam) && lParam != 0)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var point = new Point(data.pt.x, data.pt.y);

                if (!IsInsideMenu(point) && IsHandleCreated)
                {
                    Diagnostics.Log($"MENU dismissed by outside click at {point.X},{point.Y}");
                    BeginInvoke(() => Close(ToolStripDropDownCloseReason.AppClicked));
                }
            }
        }
        catch (Exception ex)
        {
            // An exception escaping a hook callback crosses back into unmanaged
            // input dispatch, where it takes the process down. Nothing this
            // method decides is worth that.
            Diagnostics.Log($"MENU mouse hook callback failed: {ex.Message}");
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private static bool IsButtonDown(int message) =>
        message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN;

    /// <summary>
    /// Whether a screen point lands on this menu or on any submenu it currently
    /// has open.
    /// <para>
    /// The submenus have to be included. Each is its own top-level window and
    /// sits outside the parent's rectangle, so testing this menu alone would
    /// read a click on a window in the Windows list as a click outside the menu
    /// - closing it out from under the click that was selecting from it.
    /// </para>
    /// </summary>
    private bool IsInsideMenu(Point screenPoint)
    {
        if (Visible && Bounds.Contains(screenPoint))
        {
            return true;
        }

        foreach (var item in Items.OfType<ToolStripMenuItem>())
        {
            if (item.HasDropDownItems
                && item.DropDown.Visible
                && item.DropDown.Bounds.Contains(screenPoint))
            {
                return true;
            }
        }

        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (IsBareAltKey(m))
        {
            // Swallowed, not forwarded. Returning without calling base means
            // WinForms never sees the keystroke that would start the close.
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Whether this message is Alt on its own.
    /// <para>
    /// Alt dismisses a menu on Windows by convention, which is ordinarily
    /// correct and here is actively harmful: Windows Magnifier zooms on Ctrl+Alt
    /// with the scroll wheel, so a user magnifying this menu in order to read it
    /// destroyed it with the same gesture.
    /// </para>
    /// <para>
    /// Deliberately only the bare Alt key. <c>WM_SYSKEYDOWN</c> also carries
    /// Alt+letter combinations, which are how a menu's access keys work, and
    /// swallowing those would take keyboard navigation away from the menu in the
    /// course of protecting it. <c>WM_SYSCHAR</c> is included for the same
    /// reason it is usually handled - an unswallowed one produces the warning
    /// beep once the key it belongs to has gone unhandled.
    /// </para>
    /// </summary>
    private static bool IsBareAltKey(Message m)
    {
        if (m.Msg is not (WM_SYSKEYDOWN or WM_SYSKEYUP or WM_SYSCHAR))
        {
            return false;
        }

        return (m.WParam.ToInt64() & 0xFFFF) == VK_MENU;
    }

    /// <summary>
    /// Refuses Alt through the command-key path as well, and supplies the Escape
    /// dismissal that turning <see cref="ToolStripDropDown.AutoClose"/> off would
    /// otherwise take away.
    /// <para>
    /// Escape only reaches here while the menu holds the keyboard focus, and the
    /// foreground churn that motivated this class takes that focus away. It is
    /// therefore a convenience, not the dismissal this menu relies on - clicking
    /// outside it is.
    /// </para>
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Menu or (Keys.Menu | Keys.Alt)
            or (Keys.Alt | Keys.Control | Keys.Menu)
            or (Keys.Alt | Keys.Shift | Keys.Menu))
        {
            // Handled, so it goes no further.
            return true;
        }

        if (keyData == Keys.Escape)
        {
            Close(ToolStripDropDownCloseReason.Keyboard);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        // The hook is unmanaged state owned by this menu. Leaving one installed
        // past the menu's life means every mouse event on the desktop still
        // calls into it.
        RemoveMouseHook();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
}
