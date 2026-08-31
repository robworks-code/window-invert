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

        // Run-key values are parsed as a command line at logon, so an unquoted path
        // containing spaces (e.g. an install under "C:\Program Files\...") is
        // ambiguous. Quote it, matching how every other Run-key writer on Windows
        // does this.
        var path = Environment.ProcessPath ?? Application.ExecutablePath;
        key.SetValue(ValueName, $"\"{path}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
