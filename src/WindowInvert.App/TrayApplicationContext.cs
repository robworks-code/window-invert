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

    public TrayApplicationContext()
    {
        _registry = new WindowRegistry(new Win32WindowApi());

        _windowsMenu = new ToolStripMenuItem("Windows");
        _menu = new ContextMenuStrip();
        _menu.Items.Add(_windowsMenu);
        _menu.Items.Add(new ToolStripSeparator());
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
            var button = new TitleBarButtonWindow(info.Rect, () => ToggleInvert(info.Hwnd, _registry.TrackedWindows[info.Hwnd].Rect));
            button.Show();
            _titleBarButtons[info.Hwnd] = button;

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
            var button = new TitleBarButtonWindow(info.Rect, () => ToggleInvert(info.Hwnd, _registry.TrackedWindows[info.Hwnd].Rect));
            button.Show();
            _titleBarButtons[info.Hwnd] = button;
        }
    }

    private void ToggleInvert(nint hwnd, WindowRect currentRect)
    {
        var isNowInverted = _invertedWindows.Toggle(hwnd);

        if (isNowInverted)
        {
            var overlay = new InvertOverlayWindow(currentRect);
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

        foreach (var info in _registry.TrackedWindows.Values.OrderBy(w => w.Title))
        {
            var item = new ToolStripMenuItem(info.Title)
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
