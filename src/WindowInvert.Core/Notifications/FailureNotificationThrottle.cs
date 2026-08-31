namespace WindowInvert.Core.Notifications;

/// <summary>
/// Decides whether a failure is worth interrupting the user about yet.
/// <para>
/// This exists because the two obvious answers are both wrong. Reporting every
/// failure is noise: losing the graphics device fails every overlay at once, so one
/// event produces a queue of identical notifications. Reporting only the first
/// failure of the session is worse, and was the previous behaviour - this app
/// registers to start at logon and then runs for days, so a transient failure a
/// minute after logon permanently consumed the one notification, and a real failure
/// hours later took an overlay away with nothing said at all. The only remaining
/// channel at that point was a <c>Debug.WriteLine</c>, which is compiled out of the
/// build the user actually runs.
/// </para>
/// <para>
/// A time window collapses the burst and still speaks up for a genuinely new
/// failure. The clock is injectable so this is testable without waiting.
/// </para>
/// </summary>
public sealed class FailureNotificationThrottle
{
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset? _lastReported;

    public FailureNotificationThrottle(TimeSpan interval, Func<DateTimeOffset>? clock = null)
    {
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval), interval, "The quiet interval cannot be negative.");
        }

        _interval = interval;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Whether to report now. Returns <see langword="true"/> for the first call and
    /// for the first call after the quiet interval has elapsed, and records that
    /// moment as the most recent report.
    /// <para>
    /// Suppressed calls deliberately do <b>not</b> extend the quiet interval. A
    /// failure that repeats every second would otherwise silence the throttle
    /// forever, which is the failure mode this type exists to remove.
    /// </para>
    /// </summary>
    public bool ShouldReport()
    {
        var now = _clock();

        if (_lastReported is { } last && now - last < _interval)
        {
            return false;
        }

        _lastReported = now;
        return true;
    }
}
