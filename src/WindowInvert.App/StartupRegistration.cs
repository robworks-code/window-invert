using Microsoft.Win32;

namespace WindowInvert.App;

/// <summary>
/// Reads and writes the per-user "run at startup" registration for this app in the
/// standard HKCU Run key. The key path and value name are overridable (not const) so
/// tests can point this at a throwaway subkey instead of the real, machine-wide Run
/// key - see tests/WindowInvert.App.Tests/StartupRegistrationTests.cs.
/// </summary>
internal static class StartupRegistration
{
    private const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultValueName = "WindowInvert";

    /// <summary>
    /// Registry key path under HKCU. Defaults to the real Run key; overridable for tests.
    /// </summary>
    internal static string RunKeyPath { get; set; } = DefaultRunKeyPath;

    /// <summary>
    /// Value name written under <see cref="RunKeyPath"/>. Overridable for tests.
    /// </summary>
    internal static string ValueName { get; set; } = DefaultValueName;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        // The value kind is stated rather than inferred. SetValue's two-argument
        // overload picks a kind from the object's runtime type, which happens to
        // give REG_SZ for a string today - but the Run key only accepts REG_SZ and
        // REG_EXPAND_SZ, and a silently different kind would be a logon-time
        // failure with nothing to see at write time.
        key.SetValue(ValueName, CurrentCommandLine, RegistryValueKind.String);
    }

    /// <summary>
    /// Points an existing registration at the copy of the app that is running now,
    /// if it names somewhere else. Installing over a copy that had registered itself
    /// from a build directory would otherwise leave logon launching a binary that
    /// the installer never touched, or that no longer exists. Does nothing when the
    /// app is not registered: this is a repair, not an opt-in.
    /// </summary>
    /// <returns>True if the registration was rewritten.</returns>
    public static bool RefreshIfStale()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key?.GetValue(ValueName) is not string registered)
        {
            return false;
        }

        if (string.Equals(registered, CurrentCommandLine, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Enable();
        return true;
    }

    /// <summary>
    /// Run-key values are parsed as a command line at logon, so an unquoted path
    /// containing spaces (e.g. an install under "C:\Program Files\...") is
    /// ambiguous. Quote it, matching how every other Run-key writer on Windows
    /// does this.
    /// </summary>
    private static string CurrentCommandLine =>
        $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"";

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
