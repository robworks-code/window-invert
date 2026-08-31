using System.Diagnostics;
using WindowInvert.Core.Geometry;
using WindowInvert.Core.InvertState;
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

    public TrayApplicationContext()
    {
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
            _activePicker = new WindowPickerOverlay(hwnd =>
            {
                if (_registry.TrackedWindows.TryGetValue(hwnd, out var info))
                {
                    ToggleInvert(hwnd, _registry.TrackedWindows[hwnd].Rect);
                    RebuildWindowsMenu();
                }
                _activePicker = null;
            });
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
        };

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
    }

    private void ToggleInvert(nint hwnd, WindowRect currentRect)
    {
        var isNowInverted = _invertedWindows.Toggle(hwnd);

        if (isNowInverted)
        {
            InvertOverlayWindow overlay;

            try
            {
                overlay = new InvertOverlayWindow(currentRect, hwnd);
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
        base.ExitThreadCore();
    }
}
