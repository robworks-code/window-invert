using System.Diagnostics;
using WindowInvert.Core.Geometry;
using WindowInvert.Core.InvertState;
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
    /// Whether the user has already been told that something failed. One balloon
    /// per session: a lost graphics device fails every overlay at once, and a queue
    /// of identical notifications is noise, not information.
    /// </summary>
    private bool _failureReported;

    public TrayApplicationContext()
    {
        // Forces the handle onto this thread, which is the UI thread. Without a
        // handle there is nothing to post to.
        _ = _uiMarshal.Handle;

        _registry = new WindowRegistry(new Win32WindowApi());

        _windowsMenu = new ToolStripMenuItem("Windows");
        _menu = new ContextMenuStrip();
        _menu.Items.Add(_windowsMenu);
        _menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupRegistration.IsEnabled,
            CheckOnClick = false,
        };
        startupItem.Click += (_, _) =>
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
        };
        _menu.Items.Add(startupItem);

        _menu.Items.Add("Pick a window...", null, (_, _) =>
        {
            // Held in a field, not a local: this object owns a native window
            // procedure, and nothing else references it once Show() returns, so
            // without a rooted reference the garbage collector is free to take it
            // away while Windows is still calling into it.
            _activePicker = new WindowPickerOverlay(
                hwnd =>
                {
                    if (_registry.TrackedWindows.TryGetValue(hwnd, out var info))
                    {
                        ToggleInvert(hwnd, info.Rect);
                        RebuildWindowsMenu();
                    }

                    _activePicker = null;
                },
                onCancelled: () => _activePicker = null);
            _activePicker.Show();
        });
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
            if (_overlays.Remove(hwnd, out var overlay))
            {
                overlay.Destroy();
            }
            if (_titleBarButtons.Remove(hwnd, out var button))
            {
                button.Destroy();
            }
            RebuildWindowsMenu();
        };

        _registry.WindowGeometryChanged += info =>
        {
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
        _registry.WindowForegroundChanged += RestackWindow;

        _registry.WindowVisibilityChanged += info =>
        {
            if (_overlays.TryGetValue(info.Hwnd, out var overlay))
            {
                if (info.IsMinimized) overlay.Hide();
                else overlay.Show();
            }

            if (_titleBarButtons.TryGetValue(info.Hwnd, out var button))
            {
                if (info.IsMinimized) button.Hide();
                else button.Show();
            }
        };

        _hook.WindowEvent += (type, hwnd) => _registry.HandleWinEvent(type, hwnd);

        BootstrapRegistry();
        _hook.Start();
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
            info.Rect,
            () => ToggleInvert(info.Hwnd, _registry.TrackedWindows[info.Hwnd].Rect));
        button.SetToggledVisual(_invertedWindows.IsInverted(info.Hwnd));
        button.Show();
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
    /// Cheap enough to call on every foreground change and every geometry change:
    /// it is at most two <c>SetWindowPos</c> calls, and none at all for a window
    /// with neither surface.
    /// </para>
    /// </summary>
    private void RestackWindow(nint sourceHwnd)
    {
        var overlayHandle = _overlays.TryGetValue(sourceHwnd, out var overlay) ? overlay.Handle : 0;
        var buttonHandle = _titleBarButtons.TryGetValue(sourceHwnd, out var button) ? button.Handle : 0;

        if (overlayHandle == 0 && buttonHandle == 0)
        {
            return;
        }

        var anchor = WindowStacking.GetWindowAbove(sourceHwnd);

        foreach (var placement in OverlayStacking.PlanRestack(anchor, overlayHandle, buttonHandle))
        {
            WindowStacking.InsertBelow(placement.Hwnd, placement.PlaceBelow);
        }
    }

    private void ToggleInvert(nint hwnd, WindowRect currentRect)
    {
        var isNowInverted = _invertedWindows.Toggle(hwnd);

        if (isNowInverted)
        {
            InvertOverlayWindow overlay = null!;

            try
            {
                // The failure callback closes over the very variable being assigned,
                // because a frame can fail before the constructor returns. That is
                // safe: the callback only posts, and the posted work runs on the UI
                // thread after this method has either completed the assignment or
                // rolled the toggle back - and in the rollback case it finds no
                // overlay registered for this window and does nothing.
                overlay = new InvertOverlayWindow(
                    currentRect,
                    hwnd,
                    error => PostToUi(() => HandleOverlayFailure(hwnd, overlay, error)));
            }
            catch (Exception ex)
            {
                // Building the overlay can fail for reasons that are transient or
                // specific to one window - capture unsupported, the source window
                // closing mid-toggle, a graphics device that would not create. The
                // constructor releases whatever it acquired, so the only thing left
                // to undo is this method's own state change. Rolling it back keeps
                // the tray menu and the title-bar button matching reality and lets
                // the user simply try again; leaving it set would show the window as
                // inverted forever with no overlay and no way to clear it, and
                // letting the exception escape a WinForms click handler would put an
                // unhandled-exception dialog on screen.
                _invertedWindows.Remove(hwnd);
                Debug.WriteLine($"Toggling invert on 0x{hwnd:X} failed: {ex}");
                return;
            }

            overlay.Show();
            _overlays[hwnd] = overlay;

            // Straight away, not only on the next move or resize. The overlay was
            // created after the toggle button, so without this the button spends the
            // interval buried under the overlay it just produced - and the button's
            // toggled colour is the only on-screen confirmation that invert is on.
            RestackWindow(hwnd);
        }
        else if (_overlays.Remove(hwnd, out var overlay))
        {
            overlay.Destroy();
        }

        if (_titleBarButtons.TryGetValue(hwnd, out var button))
        {
            button.SetToggledVisual(isNowInverted);
        }
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

        _overlays.Remove(hwnd);
        _invertedWindows.Remove(hwnd);

        try
        {
            failed.Destroy();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Destroying the failed overlay for 0x{hwnd:X} failed: {ex}");
        }

        if (_titleBarButtons.TryGetValue(hwnd, out var button))
        {
            button.SetToggledVisual(false);
        }

        RebuildWindowsMenu();
        ReportFailure(error);
    }

    /// <summary>
    /// Tells the user, once. Deliberately not <c>Debug.WriteLine</c>, which is
    /// compiled out of the Release build the user actually runs - the diagnostic
    /// that only exists in a debug build is not a diagnostic.
    /// </summary>
    private void ReportFailure(Exception error)
    {
        Debug.WriteLine($"Overlay pipeline failed: {error}");

        if (_failureReported)
        {
            return;
        }

        _failureReported = true;

        try
        {
            _trayIcon.ShowBalloonTip(
                10_000,
                "Window Invert",
                "Inverting stopped for a window because the screen capture failed. "
                + "That window is no longer inverted - switch it on again to retry.",
                ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Showing the failure balloon failed: {ex}");
        }
    }

    private void RebuildWindowsMenu()
    {
        _windowsMenu.DropDownItems.Clear();

        // Untitled windows are tracked but not listed - a menu full of blank
        // entries is worse than a short menu. The one exception is a window that is
        // currently inverted: it has to stay reachable so it can be switched back
        // off, even if it never had a title (the click-to-pick path can invert one).
        var listed = _registry.TrackedWindows.Values
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
                ToggleInvert(info.Hwnd, _registry.TrackedWindows[info.Hwnd].Rect);
                item.Checked = _invertedWindows.IsInverted(info.Hwnd);
            };

            _windowsMenu.DropDownItems.Add(item);
        }

        if (_windowsMenu.DropDownItems.Count == 0)
        {
            _windowsMenu.DropDownItems.Add(new ToolStripMenuItem("(no windows found)") { Enabled = false });
        }
    }

    protected override void ExitThreadCore()
    {
        _hook.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _uiMarshal.Dispose();
        base.ExitThreadCore();
    }
}
