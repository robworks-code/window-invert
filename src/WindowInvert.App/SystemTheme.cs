using Microsoft.Win32;

namespace WindowInvert.App;

/// <summary>
/// Whether Windows is currently asking applications to render themselves dark.
/// <para>
/// The key path is overridable (not const) so tests can point this at a
/// throwaway subkey rather than depending on whatever the machine running them
/// happens to be set to - the same arrangement
/// <see cref="StartupRegistration"/> uses, and for the same reason.
/// </para>
/// </summary>
internal static class SystemTheme
{
    private const string DefaultPersonalizeKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string DefaultValueName = "AppsUseLightTheme";

    internal static string PersonalizeKeyPath { get; set; } = DefaultPersonalizeKeyPath;

    internal static string ValueName { get; set; } = DefaultValueName;

    /// <summary>
    /// True when apps are set to dark.
    /// <para>
    /// <c>AppsUseLightTheme</c> is a <c>DWORD</c> that is 0 for dark and 1 for
    /// light. Note that it is the <i>apps</i> value: Windows has a separate
    /// <c>SystemUsesLightTheme</c> for the taskbar and Start, and the two are
    /// independently settable. A context menu belonging to an application
    /// follows the apps value, so that is the one read here.
    /// </para>
    /// <para>
    /// Anything other than a readable 0 is treated as light: a missing key, a
    /// missing value, or a value of an unexpected type. Light is what Windows
    /// itself falls back to, and it is the safer default of the two - a light
    /// menu under a dark theme looks wrong, while a dark menu under a light
    /// theme is dark text on dark ground for anyone whose contrast needs are
    /// the reason they are running this app at all.
    /// </para>
    /// </summary>
    public static bool IsDarkMode
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
            return key?.GetValue(ValueName) is int value && value == 0;
        }
    }
}
