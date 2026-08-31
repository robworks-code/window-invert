using Microsoft.Win32;
using WindowInvert.App;
using Xunit;

namespace WindowInvert.App.Tests;

/// <summary>
/// Exercises SystemTheme against a throwaway HKCU subkey rather than the real
/// Personalize key, so these assertions do not depend on how the machine
/// running them happens to be themed, and never change it.
/// </summary>
public sealed class SystemThemeTests : IDisposable
{
    private readonly string _throwawayKeyPath =
        $@"Software\WindowInvertTests-{Guid.NewGuid():N}\Personalize";
    private readonly string _originalKeyPath;
    private readonly string _originalValueName;

    public SystemThemeTests()
    {
        _originalKeyPath = SystemTheme.PersonalizeKeyPath;
        _originalValueName = SystemTheme.ValueName;

        SystemTheme.PersonalizeKeyPath = _throwawayKeyPath;
        SystemTheme.ValueName = "AppsUseLightThemeTest";
    }

    private void WriteValue(object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_throwawayKeyPath);
        key.SetValue("AppsUseLightThemeTest", value, kind);
    }

    [Fact]
    public void Zero_MeansDark()
    {
        WriteValue(0, RegistryValueKind.DWord);

        Assert.True(SystemTheme.IsDarkMode);
    }

    [Fact]
    public void One_MeansLight()
    {
        WriteValue(1, RegistryValueKind.DWord);

        Assert.False(SystemTheme.IsDarkMode);
    }

    /// <summary>
    /// The three ways of not getting an answer, all of which must fall back to
    /// light. Dark is the more damaging default to guess wrong: a dark palette
    /// applied under a light theme puts the menu's own dark text on a dark
    /// background for the one user who most needs to read it.
    /// </summary>
    [Fact]
    public void AMissingKey_IsLight()
    {
        Assert.False(SystemTheme.IsDarkMode);
    }

    [Fact]
    public void AMissingValue_IsLight()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_throwawayKeyPath))
        {
            key.SetValue("SomethingElse", 0, RegistryValueKind.DWord);
        }

        Assert.False(SystemTheme.IsDarkMode);
    }

    [Fact]
    public void AValueOfTheWrongType_IsLight()
    {
        WriteValue("0", RegistryValueKind.String);

        Assert.False(SystemTheme.IsDarkMode);
    }

    public void Dispose()
    {
        SystemTheme.PersonalizeKeyPath = _originalKeyPath;
        SystemTheme.ValueName = _originalValueName;

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
