using WindowInvert.Core.Notifications;
using Xunit;

namespace WindowInvert.Core.Tests.Notifications;

public class FailureNotificationThrottleTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Read() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    [Fact]
    public void FirstFailure_IsReported()
    {
        var clock = new FakeClock();
        var throttle = new FailureNotificationThrottle(TimeSpan.FromMinutes(5), clock.Read);

        Assert.True(throttle.ShouldReport());
    }

    [Fact]
    public void BurstOfFailuresAtTheSameInstant_ReportsOnlyTheFirst()
    {
        // Losing the graphics device fails every overlay at once. The user gets one
        // notification, not one per inverted window.
        var clock = new FakeClock();
        var throttle = new FailureNotificationThrottle(TimeSpan.FromMinutes(5), clock.Read);

        var reported = new[]
        {
            throttle.ShouldReport(),
            throttle.ShouldReport(),
            throttle.ShouldReport(),
            throttle.ShouldReport(),
        };

        Assert.Equal(new[] { true, false, false, false }, reported);
    }

    [Fact]
    public void FailureAfterTheQuietInterval_IsReportedAgain()
    {
        // The regression this type exists for: the app starts at logon and runs for
        // days, so a transient failure minutes in must not consume the only
        // notification an unrelated failure hours later would have had.
        var clock = new FakeClock();
        var throttle = new FailureNotificationThrottle(TimeSpan.FromMinutes(5), clock.Read);

        Assert.True(throttle.ShouldReport());

        clock.Advance(TimeSpan.FromHours(3));

        Assert.True(throttle.ShouldReport());
    }

    [Fact]
    public void FailureJustInsideTheQuietInterval_IsStillSuppressed()
    {
        var clock = new FakeClock();
        var throttle = new FailureNotificationThrottle(TimeSpan.FromMinutes(5), clock.Read);
        Assert.True(throttle.ShouldReport());

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1));

        Assert.False(throttle.ShouldReport());
    }

    [Fact]
    public void SuppressedFailures_DoNotExtendTheQuietInterval()
    {
        // A failure repeating every second must not hold the throttle shut forever.
        // Only a report restarts the clock.
        var clock = new FakeClock();
        var throttle = new FailureNotificationThrottle(TimeSpan.FromMinutes(5), clock.Read);
        Assert.True(throttle.ShouldReport());

        for (var i = 0; i < 240; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            throttle.ShouldReport();
        }

        // Four minutes of suppressed calls have gone by; one more minute reaches the
        // interval measured from the single report, and it opens.
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(throttle.ShouldReport());
    }

    [Fact]
    public void NegativeInterval_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FailureNotificationThrottle(TimeSpan.FromSeconds(-1)));
    }
}
