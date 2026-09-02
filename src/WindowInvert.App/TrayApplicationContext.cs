using System.Diagnostics;
using Microsoft.Win32;
using WindowInvert.Core.Geometry;
using WindowInvert.Core.InvertState;
using WindowInvert.Core.Notifications;
using WindowInvert.Core.Stacking;
using WindowInvert.Core.WindowTracking;
using WindowInvert.Native;

namespace WindowInvert.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _windowsMenu;
    private readonly WindowRegistry _registry;
    private readonly InvertedWindowSet _invertedWindows = new();
    private readonly WinEventHookListener _hook = new();
    private readonly Dictionary<nint, InvertOverlayWindow> _overlays = new();
    private readonly Dictionary<nint, TitleBarButtonWindow> _titleBarButtons = new();
    private WindowPickerOverlay? _activePicker;

    /// <summary>
    /// An invisible, parentless control whose only job is to own a window handle on
    /// the UI thread, so work can be posted there from a thread pool thread.
    /// <para>
    /// Needed because the capture and render failures this app has to react to are
    /// raised on a thread pool thread, from inside the capture engine's own frame
    /// callback, while the engine holds its callback lock - and the reaction is to
    /// tear the overlay down, which means stopping that engine. The engine rejects
    /// that outright when it is called from its own callback thread. So the
    /// teardown is posted, never performed inline.
    /// </para>
    /// <para>
    /// A control rather than the captured <c>SynchronizationContext</c>: this
    /// constructor runs before <c>Application.Run</c>, so there is no guarantee the
    /// WinForms synchronization context has been installed yet.
    /// </para>
    /// </summary>
    private readonly Control _uiMarshal = new();

    /// <summary>
    /// Rate-limits the failure balloon. Long enough that losing the graphics device
    /// - which fails every overlay within milliseconds - produces one notification
    /// rather than a queue of identical ones, and short enough that an unrelated
    /// failure later in a session that may run for days still gets said out loud.
    /// </summary>
    private readonly FailureNotificationThrottle _failureNotifications =
        new(TimeSpan.FromMinutes(5));

    /// <summary>
    /// Set when a rebuild of the Windows submenu was skipped because the submenu
    /// was open, and flushed when it closes.
    /// </summary>
    private bool _windowsMenuNeedsRebuild;

    public TrayApplicationContext()
    {
        // Forces the handle onto this thread, which is the UI thread. Without a
        // handle there is nothing to post to.
        _ = _uiMarshal.Handle;

        _registry = new WindowRegistry(new Win32WindowApi());

        // Before the menu is built, so the first time it opens it is already
        // right rather than correcting itself on the second show.
        MenuTheme.Apply();
        SystemEvents.UserPreferenceChanged += HandleUserPreferenceChanged;

        _windowsMenu = new ToolStripMenuItem("Windows");
        _windowsMenu.DropDownClosed += (_, _) =>
        {
            if (_windowsMenuNeedsRebuild)
            {
                RebuildWindowsMenu();
            }
        };
        _menu = new MagnifiableContextMenuStrip();

        // Diagnostic only. CloseReason is the single fact that separates "our own
        // z-order work is dismissing the menu" from "WinForms is closing it for a
        // reason of its own", and it is not observable any other way.
        _menu.Opening += (_, _) => Diagnostics.Log("MENU opening");
        _menu.Closing += HandleMenuClosing;
        _menu.Closed += (_, e) =>
        {
            Diagnostics.Log($"MENU closed reason={e.CloseReason}");

            // Restacking is suppressed while the menu is up, so whatever z-order
            // changes were skipped are applied now, in one pass.
            //
            // Posted rather than called: Visible is not reliably false yet inside
            // Closed, and RestackWindow's own guard reads it - so calling
            // directly would skip the very work this is here to catch up on, and
            // do it silently.
            if (_uiMarshal.IsHandleCreated)
            {
                _uiMarshal.BeginInvoke(RestackEverything);
            }
        };
        _windowsMenu.DropDownOpening += (_, _) => Diagnostics.Log("SUBMENU opening");
        _windowsMenu.DropDownClosed += (_, _) => Diagnostics.Log("SUBMENU closed");

        _menu.Items.Add(_windowsMenu);
        _menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupRegistration.IsEnabled,
            CheckOnClick = false,
        };
        startupItem.Click += (_, _) =>
        {
            // Writing the HKCU Run key can fail - group policy, the key being
            // deleted concurrently, security software holding it open - and an
            // exception escaping a WinForms click handler puts an
            // unhandled-exception dialog on screen. Same reasoning as ToggleInvert
            // a few lines below, which already wraps its fallible call.
            //
            // Checked is left untouched on failure: it still holds the last known
            // good state, so the menu keeps matching what is actually registered
            // rather than claiming a change that did not happen.
            try
            {
                if (startupItem.Checked)
                {
                    StartupRegistration.Disable();
                }
                else
                {
                    StartupRegistration.Enable();
                }

                startupItem.Checked = StartupRegistration.IsEnabled;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Changing the start-with-Windows registration failed: {ex}");
            }
        };
        _menu.Items.Add(startupItem);

        _menu.Items.Add("Pick a window...", null, (_, _) => StartWindowPicker());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Window Invert",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _registry.WindowTracked += info =>
        {
            if (Diagnostics.IsEnabled)
            {
                Diagnostics.Log(
                    $"TRACKED 0x{info.Hwnd:X} {Diagnostics.Describe(info.Hwnd)} menuOpen={_menu.Visible}");
            }

            EnsureTitleBarButton(info);
            RebuildWindowsMenu();
        };
        _registry.WindowTitleChanged += info =>
        {
            // A window that showed itself before naming itself becomes displayable
            // here: this is where it gets its toggle button and its menu entry.
            EnsureTitleBarButton(info);
            RebuildWindowsMenu();
        };
        _registry.WindowUntracked += hwnd =>
        {
            _invertedWindows.Remove(hwnd);
            DestroyOverlay(hwnd);
            if (_titleBarButtons.Remove(hwnd, out var button))
            {
                button.Destroy();
            }
            RebuildWindowsMenu();
        };

        _registry.WindowGeometryChanged += info =>
        {
            if (Diagnostics.IsEnabled)
            {
                Diagnostics.Log($"GEOMETRY 0x{info.Hwnd:X} \"{info.Title}\" menuOpen={_menu.Visible}");
            }

            if (_overlays.TryGetValue(info.Hwnd, out var overlay))
            {
                overlay.Reposition(info.Rect);
            }

            if (_titleBarButtons.TryGetValue(info.Hwnd, out var button))
            {
                button.Reposition(info.Rect);
            }

            RestackWindow(info.Hwnd);
        };

        // Activating a window raises it above the overlay and toggle button that
        // belong to it - they cannot be owned by it, because window ownership does
        // not cross a process boundary, so Windows will not carry them along. This
        // is where they are put back. The event was previously plumbed all the way
        // to the registry and then discarded.
        _registry.WindowForegroundChanged += RestackAfterForegroundChange;

        _registry.WindowVisibilityChanged += HandleVisibilityChanged;

        _hook.WindowEvent += (type, hwnd) => _registry.HandleWinEvent(type, hwnd);

        // Hooks installed before the desktop is enumerated, deliberately. Delivery
        // is out-of-context, so nothing arrives until the message loop runs, which
        // is after this constructor returns - meaning a window that dies partway
        // through the enumeration below still delivers its destroy notification
        // afterwards and the stale entry is removed. Enumerating first left exactly
        // that window tracked forever, with a toggle button floating over whatever
        // took its place on screen. Destroy notifications for windows that were
        // already gone name handles this registry never tracked, and are ignored.
        _hook.Start();
        BootstrapRegistry();
        RebuildWindowsMenu();
    }

    private void BootstrapRegistry()
    {
        var api = new Win32WindowApi();
        var initial = WindowEnumerator.EnumTopLevelWindows()
            .Select(hwnd => new WindowInfo(
                hwnd,
                api.GetTitle(hwnd),
                api.GetProcessId(hwnd),
                api.IsMinimized(hwnd),
                api.GetRect(hwnd)));

        _registry.Bootstrap(initial);

        foreach (var info in _registry.TrackedWindows.Values)
        {
            EnsureTitleBarButton(info);
        }
    }

    /// <summary>
    /// Gives <paramref name="info"/> a floating toggle button if it deserves one
    /// and has not got one already.
    /// <para>
    /// The title is the gate, and it is read live rather than from whatever was
    /// cached when the window was first tracked, because windows routinely appear
    /// before they are named. An untitled window is tracked - it just has no
    /// affordance yet.
    /// </para>
    /// <para>
    /// A button is never removed here. A window that loses its title (some
    /// applications clear and reset it) keeps the button it already has, so an
    /// inverted window can always be switched back off.
    /// </para>
    /// </summary>
    private void EnsureTitleBarButton(WindowInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Title) || _titleBarButtons.ContainsKey(info.Hwnd))
        {
            return;
        }

        var button = new TitleBarButtonWindow(
            info.Hwnd,
            info.Rect,
            () => ToggleInvert(info.Hwnd));
        button.SetToggledVisual(_invertedWindows.IsInverted(info.Hwnd));

        // Shown only if the window it belongs to is actually on screen. A minimized
        // or hidden window is tracked and can acquire its button at any time - a
        // late title arrives for one just as readily as for a visible window - and
        // showing it unconditionally put a 20 px button over whatever really
        // occupies that part of the screen now.
        if (info.IsOnScreen)
        {
            button.Show();
        }

        _titleBarButtons[info.Hwnd] = button;
        RestackWindow(info.Hwnd);
    }

    /// <summary>
    /// Re-asserts that <paramref name="sourceHwnd"/>'s toggle button sits directly
    /// above its overlay, which sits directly above the window itself.
    /// <para>
    /// The stack is built downwards from the window currently above the source,
    /// because Win32 only offers "place this below that" - <c>SetWindowPos</c>'s
    /// <c>hWndInsertAfter</c> names the window that precedes the positioned one.
    /// Handing it the source directly would put the overlay <i>under</i> the window
    /// it is inverting, where nothing would ever be visible. Measured, not assumed.
    /// </para>
    /// <para>
    /// Band membership is settled first and the anchor read afterwards, because
    /// moving a window into or out of the topmost band changes what is above the
    /// source. Windows keeps every topmost window above every non-topmost one, so a
    /// pinned source can only be covered by an overlay that is pinned with it. See
    /// <see cref="WindowStacking.MatchBand"/> for why this is stated explicitly even
    /// though the ordering pass alone was measured to achieve it.
    /// </para>
    /// <para>
    /// Cheap enough to call on every foreground change and every geometry change:
    /// at most two <c>SetWindowPos</c> calls in the steady state, none at all for a
    /// window with neither surface, and the band calls are skipped when the band is
    /// already right.
    /// </para>
    /// </summary>
    private void RestackWindow(nint sourceHwnd)
    {
        // Nothing this method does is worth doing while the tray menu is open,
        // and doing it is actively harmful: every call reorders the topmost band
        // underneath a menu that is itself a topmost popup. The menu now survives
        // that (see MagnifiableContextMenuStrip), but the churn beneath it
        // remains a real cost - the surfaces being reordered are behind the menu
        // the user is reading. The skipped work is applied in one pass when the
        // menu closes.
        if (_menu.Visible)
        {
            Diagnostics.Log($"RESTACK skipped (menu open) source=0x{sourceHwnd:X}");
            return;
        }

        var overlayHandle = _overlays.TryGetValue(sourceHwnd, out var overlay) ? overlay.Handle : 0;
        var buttonHandle = _titleBarButtons.TryGetValue(sourceHwnd, out var button) ? button.Handle : 0;

        if (overlayHandle == 0 && buttonHandle == 0)
        {
            return;
        }

        // Diagnostic only. This is the call that reorders the topmost band, which
        // is the suspected cause of the menu being dismissed while it is open.
        if (Diagnostics.IsEnabled)
        {
            Diagnostics.Log(
                $"RESTACK source=0x{sourceHwnd:X} overlay=0x{overlayHandle:X} button=0x{buttonHandle:X}"
                + $" menuOpen={_menu.Visible}");
        }

        // One retry, because every input here can go stale between reading it and
        // acting on it: the anchor can be destroyed, and the source's band can
        // change. A half-applied plan is worse than either - the button moves and
        // the overlay does not, which can leave the overlay below the source.
        if (!TryRestack(sourceHwnd, overlayHandle, buttonHandle)
            && !TryRestack(sourceHwnd, overlayHandle, buttonHandle))
        {
            // Every other fallible path in this class says so when it gives up. A
            // silently half-applied z-order is the one failure here that looks
            // exactly like working software, so it is the last thing that should
            // fail quietly.
            Debug.WriteLine($"Restack failed twice for window {sourceHwnd:X}; z-order may be half-applied.");
        }
    }

    /// <summary>
    /// One attempt at the restack. Returns false if any step failed, in which case
    /// the z-order may be half-applied and the whole sequence should be retried
    /// from a freshly read anchor.
    /// </summary>
    private static bool TryRestack(nint sourceHwnd, nint overlayHandle, nint buttonHandle)
    {
        var sourceIsTopmost = WindowStacking.IsTopmost(sourceHwnd);

        // Checked like every other step, not fire-and-forget. A failed band change
        // leaves the surface in the wrong band, where the ordering pass below cannot
        // lift it past the source - which is the silent "invert did nothing" shape.
        if (!WindowStacking.MatchBand(overlayHandle, sourceIsTopmost)
            || !WindowStacking.MatchBand(buttonHandle, sourceIsTopmost))
        {
            return false;
        }

        // Read after the band changes, not before: moving a window into or out of
        // the topmost band changes what is above the source.
        var anchor = WindowStacking.GetWindowAbove(sourceHwnd);

        foreach (var placement in OverlayStacking.PlanRestack(anchor, overlayHandle, buttonHandle))
        {
            if (!WindowStacking.InsertBelow(placement.Hwnd, placement.PlaceBelow))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Re-asserts the z-order of the window that just came to the foreground, the
    /// window that owns it, and every window that currently has an overlay.
    /// <para>
    /// Restacking only the reported hwnd is not enough, because the foreground
    /// event names the window that was activated and that need not be the window
    /// that got raised. Activating a dialog raises the window that owns it, and the
    /// dialog itself is never tracked - owned windows are exactly what the tracking
    /// predicate excludes - so nothing would restack the owner, and its surfaces
    /// would stay buried until the user next clicked or moved the window itself.
    /// That is not the brief activation flash; it persists. Resolving the owner
    /// answers that case exactly, in one <c>GetAncestor</c> call.
    /// </para>
    /// <para>
    /// The owner walk is what covers toggle buttons, which are the far larger set -
    /// nearly every window on the desktop has one, while only the inverted few have
    /// an overlay. Sweeping all of them on every activation would cost dozens of
    /// <c>SetWindowPos</c> calls for no reason: activation raises the activated
    /// window and its owner group, and nothing else moves.
    /// </para>
    /// <para>
    /// The overlay sweep stays as the belt-and-braces pass, and costs almost
    /// nothing: there are typically one to three inverted windows, at two
    /// <c>SetWindowPos</c> calls each. Order does not matter - each window's stack
    /// reads its own anchor when it is applied.
    /// </para>
    /// </summary>
    private void RestackAfterForegroundChange(nint foregroundHwnd)
    {
        if (Diagnostics.IsEnabled)
        {
            Diagnostics.Log(
                $"FOREGROUND 0x{foregroundHwnd:X} {Diagnostics.Describe(foregroundHwnd)} menuOpen={_menu.Visible}");
        }

        RestackWindow(foregroundHwnd);

        var ownerRoot = WindowStacking.GetOwnerRoot(foregroundHwnd);
        if (ownerRoot != 0 && ownerRoot != foregroundHwnd)
        {
            RestackWindow(ownerRoot);
        }

        foreach (var hwnd in _overlays.Keys.ToArray())
        {
            if (hwnd != foregroundHwnd && hwnd != ownerRoot)
            {
                RestackWindow(hwnd);
            }
        }
    }

    /// <summary>
    /// Records why the menu closed. Diagnostics only - nothing here refuses a
    /// close, deliberately.
    /// <para>
    /// Cancelling was tried, for two different reasons, and both attempts made
    /// things worse in the same way. By the time <c>Closing</c> is raised Windows
    /// has already left menu mode, so the dropdown has given up its capture and
    /// focus; refusing the close leaves a menu on screen that nothing can dismiss
    /// afterwards - not Escape, not clicking elsewhere, not another window taking
    /// the foreground. Two traces showed it plainly: one cancelled close, then no
    /// further close attempt at all while the menu sat open for minutes across
    /// dozens of foreground changes.
    /// </para>
    /// <para>
    /// So the menu is kept alive one step earlier instead, in
    /// <see cref="MagnifiableContextMenuStrip"/>: Alt is refused as a keystroke
    /// before any close is initiated, and <c>AutoClose</c> is off so a foreground
    /// change never asks for one. The causes that were this app's own doing are
    /// fixed at source - the tray overflow flyout is no longer tracked, and
    /// restacking is suspended while the menu is open.
    /// </para>
    /// <para>
    /// Kept because the reason code is still the only way to tell an intended
    /// dismissal from another one appearing from somewhere unexamined.
    /// </para>
    /// </summary>
    private static void HandleMenuClosing(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        // Cancel is logged because it arrives PRE-SET: WinForms cancels every
        // close of a non-AutoClose drop-down except CloseCalled before this
        // event is even raised. A trace full of closing lines with cancelled=True
        // and no closed line is that veto at work, not a rogue subscriber - it
        // cost a day to learn the difference.
        Diagnostics.Log(
            $"MENU closing reason={e.CloseReason} cancelled={e.Cancel} modifiers={Control.ModifierKeys}");
    }

    /// <summary>
    /// Restacks every surface this app owns. Used to catch up after the tray menu
    /// closes, since restacking is suppressed while it is open.
    /// </summary>
    private void RestackEverything()
    {
        foreach (var hwnd in _overlays.Keys.ToArray())
        {
            RestackWindow(hwnd);
        }

        foreach (var hwnd in _titleBarButtons.Keys.ToArray())
        {
            if (!_overlays.ContainsKey(hwnd))
            {
                RestackWindow(hwnd);
            }
        }
    }

    /// <summary>
    /// Follows a window on and off the screen - minimized, restored, hidden to a
    /// notification area, or cloaked onto another virtual desktop and back.
    /// <para>
    /// The overlay is destroyed while the window is away rather than merely hidden,
    /// and rebuilt when it returns. Hiding it would leave a capture session running
    /// against a window that composes no frames, which both holds graphics resources
    /// for nothing and invites the capture pipeline's own failure path to fire for a
    /// window that is simply minimized - taking the user's invert setting off, with
    /// a warning, for no real fault.
    /// </para>
    /// <para>
    /// The inverted flag itself survives all of this. It records what the user
    /// asked for, not what is currently on screen, so a window that comes back comes
    /// back inverted.
    /// </para>
    /// </summary>
    private void HandleVisibilityChanged(WindowInfo info)
    {
        if (_titleBarButtons.TryGetValue(info.Hwnd, out var button))
        {
            if (info.IsOnScreen)
            {
                button.Show();
            }
            else
            {
                button.Hide();
            }
        }

        if (!info.IsOnScreen)
        {
            DestroyOverlay(info.Hwnd);
            RebuildWindowsMenu();
            return;
        }

        if (_invertedWindows.IsInverted(info.Hwnd) && !TryShowOverlay(info.Hwnd, info.Rect))
        {
            // Rebuilding on return can fail exactly as creating it can, and here the
            // user did nothing to prompt it - so unlike a toggle they clicked, there
            // is nothing on screen that would explain a window coming back
            // un-inverted. TryShowOverlay has already reported it; this clears the
            // state so the menu and the button agree with what is actually rendered.
            ClearInvert(info.Hwnd);
        }

        // Restoring usually activates the window too, and the foreground event
        // would then repair the order - but not every restore activates, and
        // showing a window puts it at the top of its band regardless. Same
        // assertion, one more trigger point.
        RestackWindow(info.Hwnd);
        RebuildWindowsMenu();
    }

    /// <summary>
    /// Turns inversion on or off for <paramref name="hwnd"/>, reading the window's
    /// live geometry itself.
    /// <para>
    /// The rect is looked up here rather than passed in, and that is load-bearing
    /// rather than tidiness. Every caller is a closure that outlives the window it
    /// captured - a tray-menu item the user is still looking at, a floating toggle
    /// button still on screen, a click-to-pick callback - and each one used to index
    /// the tracked-window dictionary to build the argument. An entry removed between
    /// the closure being created and the user clicking it therefore threw
    /// <c>KeyNotFoundException</c> out of a WinForms click handler and put an
    /// unhandled-exception dialog on screen. Owning the lookup means there is one
    /// guard instead of three, and no way to add a fourth call site that skips it.
    /// </para>
    /// </summary>
    private void ToggleInvert(nint hwnd)
    {
        if (!_registry.TrackedWindows.TryGetValue(hwnd, out var info))
        {
            // The window closed while its menu entry or toggle button was still on
            // screen. Nothing to toggle, and the surfaces have already been torn
            // down by the untracking path - just bring the menu back in line.
            Debug.WriteLine($"Ignoring an invert toggle for untracked window 0x{hwnd:X}.");
            RebuildWindowsMenu();
            return;
        }

        var isNowInverted = _invertedWindows.Toggle(hwnd);

        if (!isNowInverted)
        {
            DestroyOverlay(hwnd);
        }
        else if (info.IsOnScreen && !TryShowOverlay(hwnd, info.Rect))
        {
            // Rolling the toggle back keeps the tray menu and the button matching
            // reality and lets the user simply try again; leaving it set would show
            // the window as inverted forever with no overlay behind the claim.
            _invertedWindows.Remove(hwnd);
            isNowInverted = false;
        }

        // An off-screen window keeps the flag with no overlay behind it. That is the
        // whole point of separating the two: the overlay is built when the window
        // comes back, by HandleVisibilityChanged. Creating one now would put a
        // frozen, blank inverted rectangle over whatever really occupies the
        // window's last known position.

        if (_titleBarButtons.TryGetValue(hwnd, out var button))
        {
            button.SetToggledVisual(isNowInverted);
        }
    }

    /// <summary>
    /// Builds and shows the invert overlay for <paramref name="hwnd"/>, reporting
    /// failure rather than throwing. Returns whether an overlay is in place
    /// afterwards - including when one already was.
    /// </summary>
    private bool TryShowOverlay(nint hwnd, WindowRect rect)
    {
        if (_overlays.ContainsKey(hwnd))
        {
            return true;
        }

        InvertOverlayWindow overlay = null!;

        try
        {
            // The failure callback closes over the very variable being assigned,
            // because a frame can fail before the constructor returns. That is
            // safe: the callback only posts, and the posted work runs on the UI
            // thread after this method has either completed the assignment or
            // returned - and in the failure case it finds no overlay registered for
            // this window and does nothing.
            overlay = new InvertOverlayWindow(
                rect,
                hwnd,
                error => PostToUi(() => HandleOverlayFailure(hwnd, overlay, error)));
        }
        catch (Exception ex)
        {
            // Building the overlay can fail for reasons that are transient or
            // specific to one window - capture unsupported, the source window
            // closing mid-toggle, a graphics device that would not create. The
            // constructor releases whatever it acquired, so there is nothing here to
            // undo but the caller's own state.
            ReportPipelineFailure(ex);
            return false;
        }

        overlay.Show();
        _overlays[hwnd] = overlay;

        // Straight away, not only on the next move or resize. The overlay is created
        // after the toggle button, so without this the button spends the interval
        // buried under the overlay it just produced - and the button's toggled
        // colour is the only on-screen confirmation that invert is on.
        RestackWindow(hwnd);
        return true;
    }

    /// <summary>
    /// Removes and destroys <paramref name="hwnd"/>'s overlay if it has one, leaving
    /// the inverted flag alone. Safe to call for a window that has none.
    /// </summary>
    private void DestroyOverlay(nint hwnd)
    {
        if (!_overlays.Remove(hwnd, out var overlay))
        {
            return;
        }

        try
        {
            overlay.Destroy();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Destroying the invert overlay for 0x{hwnd:X} failed: {ex}");
        }
    }

    /// <summary>
    /// Takes inversion off <paramref name="hwnd"/> entirely - the flag, the overlay
    /// and the button's toggled colour - and brings the menu back in line.
    /// </summary>
    private void ClearInvert(nint hwnd)
    {
        _invertedWindows.Remove(hwnd);
        DestroyOverlay(hwnd);

        if (_titleBarButtons.TryGetValue(hwnd, out var button))
        {
            button.SetToggledVisual(false);
        }

        RebuildWindowsMenu();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, asynchronously. Never
    /// blocks: the caller is usually a thread pool thread holding the capture
    /// engine's lock, and waiting for a UI thread that may itself be stopping that
    /// engine would deadlock.
    /// </summary>
    private void PostToUi(Action action)
    {
        try
        {
            if (_uiMarshal.IsDisposed || !_uiMarshal.IsHandleCreated)
            {
                return;
            }

            _uiMarshal.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            // The handle can be destroyed between the check and the call while the
            // app is exiting. Nothing to report at that point.
            Debug.WriteLine($"Posting to the UI thread failed: {ex}");
        }
    }

    /// <summary>
    /// Takes down an overlay whose capture or render pipeline has failed, and makes
    /// the failure visible.
    /// <para>
    /// This is the point of the whole failure path. A stalled overlay looks exactly
    /// like a working one - correctly placed, correctly inverted, perfectly legible,
    /// and frozen - so the user carries on reading a snapshot while typing into the
    /// live window underneath it. Removing the overlay makes "not working" look
    /// different from "working", and leaves the window in a state the user can
    /// simply toggle again.
    /// </para>
    /// <para>
    /// Runs on the UI thread, posted from a thread pool thread. The identity check
    /// matters: by the time this runs the user may have toggled the window off and
    /// on again, and a stale failure must not destroy the replacement overlay.
    /// </para>
    /// </summary>
    private void HandleOverlayFailure(nint hwnd, InvertOverlayWindow failed, Exception error)
    {
        if (!_overlays.TryGetValue(hwnd, out var current) || !ReferenceEquals(current, failed))
        {
            return;
        }

        ClearInvert(hwnd);
        ReportPipelineFailure(error);
    }

    /// <summary>
    /// Logs a capture or render failure, and tells the user about it if one has not
    /// been reported recently.
    /// <para>
    /// The balloon is the only channel that exists in the build the user actually
    /// runs - <c>Debug.WriteLine</c> is compiled out of Release, and a diagnostic
    /// that only exists in a debug build is not a diagnostic. See
    /// <see cref="FailureNotificationThrottle"/> for why it is rate-limited rather
    /// than either unlimited or once per session.
    /// </para>
    /// </summary>
    private void ReportPipelineFailure(Exception error)
    {
        Debug.WriteLine($"Overlay pipeline failed: {error}");

        if (!_failureNotifications.ShouldReport())
        {
            return;
        }

        ShowBalloon(
            "Inverting stopped for a window because the screen capture failed. "
            + "That window is no longer inverted - switch it on again to retry.",
            ToolTipIcon.Warning);
    }

    private void ShowBalloon(string text, ToolTipIcon icon)
    {
        try
        {
            _trayIcon.ShowBalloonTip(10_000, "Window Invert", text, icon);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Showing a notification balloon failed: {ex}");
        }
    }

    /// <summary>
    /// Enters click-to-pick mode, unless it is already active.
    /// <para>
    /// The guard is not defensive tidying. The picker is a full-screen topmost
    /// window, and so is the tray menu, so the menu item that starts this mode stays
    /// reachable while the mode is running. Starting a second picker used to
    /// overwrite the field holding the first, leaving a full-screen click-swallowing
    /// window alive with nothing referencing it - and when the abandoned one's
    /// callback eventually ran it cleared the field, dropping the only rooted
    /// reference to the <i>new</i> picker while Windows was still calling its window
    /// procedure. The identity checks in the callbacks below make that safe even if
    /// a picker is somehow orphaned anyway.
    /// </para>
    /// </summary>
    private void StartWindowPicker()
    {
        if (_activePicker is not null)
        {
            return;
        }

        WindowPickerOverlay? picker = null;
        picker = new WindowPickerOverlay(
            hwnd =>
            {
                ClearPicker(picker);
                HandleWindowPicked(hwnd);
            },
            onCancelled: () => ClearPicker(picker));

        _activePicker = picker;
        picker.Show();
    }

    /// <summary>
    /// Clears the rooted picker reference, but only if <paramref name="picker"/> is
    /// still the current one. A callback from an abandoned picker must never unroot
    /// its successor.
    /// </summary>
    private void ClearPicker(WindowPickerOverlay? picker)
    {
        if (ReferenceEquals(_activePicker, picker))
        {
            _activePicker = null;
        }
    }

    /// <summary>
    /// Resolves what the user clicked in pick mode to a window this app can invert,
    /// and says so when it cannot.
    /// <para>
    /// A click that resolves to nothing used to leave pick mode with no action and
    /// no message, which for the app's most discoverable entry point is the wrong
    /// failure shape: the wash disappears and the user is left to work out whether
    /// anything happened. Clicking the desktop, the taskbar or a dialog box all
    /// landed here.
    /// </para>
    /// </summary>
    private void HandleWindowPicked(nint hitHwnd)
    {
        var target = ResolvePickedWindow(hitHwnd);

        if (target == 0)
        {
            ShowBalloon(
                "That is not a window this app can invert. The desktop, the taskbar and "
                + "dialog boxes cannot be inverted - pick an application window instead.",
                ToolTipIcon.Info);
            return;
        }

        ToggleInvert(target);
        RebuildWindowsMenu();
    }

    /// <summary>
    /// Maps the window under the click to the window the user meant, or 0.
    /// <para>
    /// The toggle-button case is the one worth spelling out. This app's own floating
    /// buttons are ordinary top-level windows that sit over their source's title
    /// bar, and unlike the invert overlay they are not <c>WS_EX_TRANSPARENT</c>, so
    /// a pick aimed at a window's title bar can land on one. It is never tracked, so
    /// the lookup missed and the whole gesture did nothing. A button unambiguously
    /// identifies the window it belongs to, which is the window the user was aiming
    /// at anyway.
    /// </para>
    /// </summary>
    private nint ResolvePickedWindow(nint hitHwnd)
    {
        if (hitHwnd == 0)
        {
            return 0;
        }

        if (_registry.TrackedWindows.ContainsKey(hitHwnd))
        {
            return hitHwnd;
        }

        foreach (var (source, button) in _titleBarButtons)
        {
            if (button.Handle == hitHwnd)
            {
                return source;
            }
        }

        return 0;
    }

    /// <summary>
    /// Rebuilds the Windows submenu, unless the user is looking at it.
    /// <para>
    /// Title changes have to rebuild this menu - that is how a window that named
    /// itself late gets an entry - but they are high-frequency for exactly the
    /// applications this app is used on: a download percentage, a media player's
    /// elapsed-time caption, a build progress count. Since the rebuild opens by
    /// clearing every item, one arriving while the submenu is open would delete and
    /// recreate the entries under the pointer. Someone navigating that list through
    /// a magnified viewport would have the item they were aiming at disappear
    /// mid-reach.
    /// </para>
    /// <para>
    /// A click on an item fires after the drop-down has closed, so a rebuild caused
    /// by toggling a window still runs immediately; only a rebuild arriving while
    /// the list is being browsed is deferred. The deferral is also why
    /// <see cref="ToggleInvert"/> must tolerate a handle that is no longer tracked:
    /// the entry the user finally clicks may name a window that closed while they
    /// were reading the list.
    /// </para>
    /// </summary>
    private void RebuildWindowsMenu()
    {
        if (_windowsMenu.HasDropDownItems && _windowsMenu.DropDown.Visible)
        {
            _windowsMenuNeedsRebuild = true;
            return;
        }

        _windowsMenuNeedsRebuild = false;
        _windowsMenu.DropDownItems.Clear();

        // Hidden and cloaked windows stay tracked - that is what preserves an invert
        // setting across a minimize-to-tray or a virtual desktop switch - but they
        // are not listed. There is nothing on screen to invert, and an entry that
        // silently does nothing when clicked is worse than no entry at all.
        //
        // Untitled windows are tracked but not listed either: a menu full of blank
        // entries is worse than a short menu. The one exception is a window that is
        // currently inverted, which has to stay reachable so it can be switched back
        // off, even if it never had a title (the click-to-pick path can invert one).
        var listed = _registry.TrackedWindows.Values
            .Where(w => !w.IsHidden)
            .Where(w => !string.IsNullOrWhiteSpace(w.Title) || _invertedWindows.IsInverted(w.Hwnd))
            .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase);

        foreach (var info in listed)
        {
            var label = string.IsNullOrWhiteSpace(info.Title)
                ? $"(untitled window 0x{info.Hwnd:X})"
                : info.Title;

            var item = new ToolStripMenuItem(label)
            {
                Checked = _invertedWindows.IsInverted(info.Hwnd),
                CheckOnClick = false,
            };

            item.Click += (_, _) =>
            {
                ToggleInvert(info.Hwnd);
                item.Checked = _invertedWindows.IsInverted(info.Hwnd);
            };

            _windowsMenu.DropDownItems.Add(item);
        }

        if (_windowsMenu.DropDownItems.Count == 0)
        {
            _windowsMenu.DropDownItems.Add(new ToolStripMenuItem("(no windows found)") { Enabled = false });
        }
    }

    /// <summary>
    /// Repaints the menu when the user switches Windows between light and dark
    /// while the app is running, which for an app that starts at logon and stays
    /// up for days is not a rare event.
    /// <para>
    /// <see cref="SystemEvents"/> raises this on its own thread, so the work is
    /// marshalled: touching <see cref="ToolStripManager"/> from off the UI thread
    /// is exactly the kind of thing that fails intermittently rather than
    /// loudly. The categories are broader than strictly necessary because which
    /// one Windows reports for an apps-theme change is not contractual, and
    /// re-applying an unchanged theme costs one registry read.
    /// </para>
    /// </summary>
    private void HandleUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General
            or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        if (!_uiMarshal.IsHandleCreated)
        {
            return;
        }

        try
        {
            _uiMarshal.BeginInvoke(MenuTheme.Apply);
        }
        catch (Exception ex)
        {
            // The handle can go between the check above and the post, during
            // shutdown. A theme repaint is never worth taking the app down.
            Debug.WriteLine($"Re-applying the menu theme failed: {ex}");
        }
    }

    protected override void ExitThreadCore()
    {
        // Static event, so this outlives the instance if it is not detached -
        // the handler would be called on a disposed context.
        SystemEvents.UserPreferenceChanged -= HandleUserPreferenceChanged;
        _hook.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _uiMarshal.Dispose();
        base.ExitThreadCore();
    }
}
