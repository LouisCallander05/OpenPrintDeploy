using OpenPrintDeploy.Server.Auth;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.Auth;

public sealed class LoginThrottleTests
{
    [Fact]
    public void LocksOutAfterThreshold_ThenRecoversAfterCooldown()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var throttle = new LoginThrottle(
            maxFailures: 3,
            window: TimeSpan.FromMinutes(5),
            lockout: TimeSpan.FromMinutes(5),
            now: () => now);

        Assert.False(throttle.IsLockedOut("user", out _));

        throttle.RecordFailure("user");
        throttle.RecordFailure("user");
        Assert.False(throttle.IsLockedOut("user", out _)); // below threshold

        throttle.RecordFailure("user"); // hits threshold
        Assert.True(throttle.IsLockedOut("user", out var retry));
        Assert.True(retry > TimeSpan.Zero);

        now = now.AddMinutes(5).AddSeconds(1); // cooldown elapsed
        Assert.False(throttle.IsLockedOut("user", out _));
    }

    [Fact]
    public void SuccessClearsFailures()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var throttle = new LoginThrottle(maxFailures: 3, now: () => now);

        throttle.RecordFailure("user");
        throttle.RecordFailure("user");
        throttle.RecordSuccess("user");

        // After a success the counter is reset, so two more failures don't lock.
        throttle.RecordFailure("user");
        throttle.RecordFailure("user");
        Assert.False(throttle.IsLockedOut("user", out _));
    }

    [Fact]
    public void OldFailuresOutsideWindowDoNotCount()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var throttle = new LoginThrottle(maxFailures: 3, window: TimeSpan.FromMinutes(5), now: () => now);

        throttle.RecordFailure("user");
        throttle.RecordFailure("user");
        now = now.AddMinutes(6); // first two age out of the window
        throttle.RecordFailure("user");
        throttle.RecordFailure("user");

        Assert.False(throttle.IsLockedOut("user", out _));
    }

    [Fact]
    public void ThrottleIsPerUsername()
    {
        var throttle = new LoginThrottle(maxFailures: 2);
        throttle.RecordFailure("alice");
        throttle.RecordFailure("alice");

        Assert.True(throttle.IsLockedOut("alice", out _));
        Assert.False(throttle.IsLockedOut("bob", out _));
    }
}
