namespace WindowInvert.App;

/// <summary>
/// A context menu that does not treat the Alt key as an instruction to close.
/// <para>
/// Alt dismisses a menu on Windows by convention, which is ordinarily correct
/// and here is actively harmful: screen magnifiers bind zoom to chords built on
/// Alt - Windows Magnifier's own is Ctrl+Alt with the scroll wheel - so a user
/// magnifying this menu in order to read it destroys it with the same gesture.
/// For someone who navigates by magnification that is not a rough edge, it makes
/// the menu unusable: the reported symptom was never getting far enough to
/// select anything at all.
/// </para>
/// <para>
/// The interception happens here, at the message, rather than by cancelling
/// <c>Closing</c>. Cancelling was tried first and is a trap. By the time
/// <c>Closing</c> is raised, Windows has already left menu mode, so the dropdown
/// has surrendered its capture and focus; refusing the close leaves a menu on
/// screen that can no longer be closed by anything - not Escape, not clicking
/// elsewhere, not another window taking the foreground. A trace showed exactly
/// that: one cancelled Alt close, then no further close attempt at all while the
/// menu sat open through dozens of foreground changes. Swallowing the keystroke
/// means the close is never initiated and the menu stays fully live, so every
/// ordinary way of dismissing it keeps working.
/// </para>
/// </summary>
internal sealed class MagnifiableContextMenuStrip : ContextMenuStrip
{
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_SYSCHAR = 0x0106;

    /// <summary><c>VK_MENU</c> - the Alt key itself.</summary>
    private const int VK_MENU = 0x12;

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
    /// The second half of the same guard. WinForms routes some key handling
    /// through the command-key path before it ever reaches
    /// <see cref="WndProc"/>, and the modal filter that runs menus can pick a
    /// keystroke up from there, so Alt is refused in both places rather than
    /// assuming which one wins.
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

        return base.ProcessCmdKey(ref msg, keyData);
    }
}
