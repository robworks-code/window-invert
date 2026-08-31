using Microsoft.Win32;
using WindowInvert.App;
using Xunit;

namespace WindowInvert.App.Tests;

/// <summary>
/// Exercises StartupRegistration against a throwaway HKCU subkey instead of the real,
/// machine-wide Run key. Never touches Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public sealed class StartupRegistrationTests : IDisposable
{
    // A unique-per-run subkey directly under HKCU\Software, well away from anything
    // real. Deleted in Dispose() with an explicit prefix assertion guarding the delete.
    private readonly string _throwawayKeyPath =
        $@"Software\WindowInvertTests-{Guid.NewGuid():N}\Run";
    private readonly string _originalRunKeyPath;
    private readonly string _originalValueName;

    public StartupRegistrationTests()
    {
        _originalRunKeyPath = StartupRegistration.RunKeyPath;
        _originalValueName = StartupRegistration.ValueName;

        StartupRegistration.RunKeyPath = _throwawayKeyPath;
        StartupRegistration.ValueName = "WindowInvertTest";
    }

    [Fact]
    public void IsEnabled_WhenKeyDoesNotExist_ReturnsFalse()
    {
        Assert.False(StartupRegistration.IsEnabled);
    }

    [Fact]
    public void Enable_WritesValue_AndIsEnabledReadsItBack()
    {
        StartupRegistration.Enable();

        Assert.True(StartupRegistration.IsEnabled);

        using var key = Registry.CurrentUser.OpenSubKey(_throwawayKeyPath, writable: false);
        Assert.NotNull(key);
        var value = key!.GetValue("WindowInvertTest") as string;
        Assert.False(string.IsNullOrEmpty(value));

        // Run-key values are parsed as a command line at logon, so the path must be
        // quoted (unquoted paths containing spaces are ambiguous).
        Assert.StartsWith("\"", value);
        Assert.EndsWith("\"", value);
        var innerPath = value!.Trim('"');
        Assert.False(string.IsNullOrEmpty(innerPath));
    }

    [Fact]
    public void Enable_WhenTheKeyAlreadyExists_TakesTheOpenSubKeyPath_AndStillWritesAREG_SZ()
    {
        // Every other test starts from a fresh GUID subkey, so only the
        // CreateSubKey fallback ever ran - while in production the Run key always
        // exists and the OpenSubKey path is the one that is actually taken.
        StartupRegistration.Enable();

        using (var firstKey = Registry.CurrentUser.OpenSubKey(_throwawayKeyPath, writable: false))
        {
            Assert.NotNull(firstKey);
        }

        StartupRegistration.Enable();

        Assert.True(StartupRegistration.IsEnabled);

        using var key = Registry.CurrentUser.OpenSubKey(_throwawayKeyPath, writable: false);
        Assert.NotNull(key);
        Assert.Equal(RegistryValueKind.String, key!.GetValueKind("WindowInvertTest"));

        var value = key.GetValue("WindowInvertTest") as string;
        Assert.False(string.IsNullOrEmpty(value));
        Assert.StartsWith("\"", value);
        Assert.EndsWith("\"", value);

        // Exactly one value, not a duplicate written alongside the first.
        Assert.Equal(new[] { "WindowInvertTest" }, key.GetValueNames());
    }

    [Fact]
    public void Disable_AfterEnable_RemovesValue_AndIsEnabledReturnsFalse()
    {
        StartupRegistration.Enable();
        Assert.True(StartupRegistration.IsEnabled);

        StartupRegistration.Disable();

        Assert.False(StartupRegistration.IsEnabled);
    }

    [Fact]
    public void Disable_WhenKeyNeverExisted_DoesNotThrow()
    {
        var exception = Record.Exception(() => StartupRegistration.Disable());

        Assert.Null(exception);
    }

    public void Dispose()
    {
        StartupRegistration.RunKeyPath = _originalRunKeyPath;
        StartupRegistration.ValueName = _originalValueName;

        // Never expand a bare variable into a delete path: assert the throwaway
        // prefix before deleting anything under HKCU\Software.
        var rootOfThrowaway = _throwawayKeyPath.Split('\\')[1];
        if (!rootOfThrowaway.StartsWith("WindowInvertTests-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to delete unexpected registry key: {_throwawayKeyPath}");
        }

        Registry.CurrentUser.DeleteSubKeyTree(
            $@"Software\{rootOfThrowaway}", throwOnMissingSubKey: false);
    }
}
