using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowInvert.App;

/// <summary>
/// An opt-in trace log, for diagnosing behaviour that only appears on a real
/// desktop. Off unless <c>WINDOWINVERT_DIAG</c> is set, in which case lines go
/// to <c>%TEMP%\window-invert-diag.log</c>.
/// <para>
/// This exists because the interesting failures in this application are timing
/// and z-order interactions between windows belonging to other processes. They
/// do not reproduce under a unit test and they do not survive being watched in a
/// debugger, since attaching changes which window is in the foreground. A
/// timestamped log written from the live session is the only way to see the
/// ordering of events that actually happened.
/// </para>
/// </summary>
internal static class Diagnostics
{
    private static readonly object Gate = new();

    /// <summary>
    /// Read once. The check is on the hot path - geometry notifications arrive in
    /// bursts - and an environment read per line would be a cost paid by every
    /// user to serve the rare session that is being traced.
    /// </summary>
    private static readonly bool Enabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINDOWINVERT_DIAG"));

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "window-invert-diag.log");

    public static bool IsEnabled => Enabled;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    /// <summary>
    /// A window's class and title, for identifying it in the log.
    /// <para>
    /// This has to be read at the moment of the event. Windows that appear,
    /// take the foreground and are destroyed within a second cannot be
    /// identified afterwards - the handle is dead by the time the log is read,
    /// which is exactly the position the first trace left us in.
    /// </para>
    /// </summary>
    public static string Describe(nint hwnd)
    {
        if (!Enabled)
        {
            return string.Empty;
        }

        if (hwnd == 0 || !IsWindow(hwnd))
        {
            return "(not a window)";
        }

        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);

        return $"[{cls}] \"{text}\"";
    }

    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        // Timestamps to the millisecond: the whole point is the ORDER of events
        // and how close together they are, so a whole-second stamp would throw
        // away the thing being measured.
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}";

        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            // Diagnostics must never be able to take down the thing being
            // diagnosed, or the log becomes the bug.
            Debug.WriteLine($"Diagnostic write failed: {ex.Message}");
        }
    }
}
