namespace WindowInvert.App;

internal static class Program
{
    /// <summary>
    /// Unprefixed, so it lives in the caller's session namespace: two logged-on
    /// users each get their own instance, which is right, while one user cannot
    /// start the app twice.
    /// </summary>
    private const string SingleInstanceMutexName = "WindowInvert.SingleInstance";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // A second instance is not merely redundant, it actively fights the first:
        // every window would carry two overlapping toggle buttons, and each
        // instance's restack would evict the other's from the slot directly above
        // the source on every foreground and geometry event, leaving the affordance
        // flickering permanently with nothing on screen to say why. This app
        // registers to start at logon, so "already running" is the normal state when
        // someone launches it from the Start menu.
        using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isOnlyInstance);

        if (!isOnlyInstance)
        {
            // Deliberately not a silent exit. The user asked for the app by
            // launching it, and a launch that produces nothing at all is the same
            // "it did nothing and said nothing" shape this app exists to avoid -
            // worse still for someone reading a magnified viewport, who may not have
            // the tray in view to notice the icon that is already there.
            MessageBox.Show(
                "Window Invert is already running. Its icon is in the notification area, "
                + "at the right-hand end of the taskbar.",
                "Window Invert",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayApplicationContext());
    }
}
