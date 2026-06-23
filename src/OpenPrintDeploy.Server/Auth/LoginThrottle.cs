using System.Collections.Concurrent;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>
/// Per-username throttle for admin Basic sign-in. After a run of failures it
/// locks the username out for a cooldown during which credentials are NOT
/// validated against AD at all — which both slows password-guessing and, more
/// importantly, stops a guessing attack from driving the real AD account into
/// lockout (every rejected bind would otherwise count toward the domain lockout
/// policy). Keyed on the username so the protection follows the account across
/// source IPs. In-memory and best-effort; a process restart clears it.
/// </summary>
public sealed class LoginThrottle
{
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _lockout;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public LoginThrottle(int maxFailures = 5, TimeSpan? window = null, TimeSpan? lockout = null, Func<DateTime>? now = null)
    {
        _maxFailures = Math.Max(1, maxFailures);
        _window = window ?? TimeSpan.FromMinutes(5);
        _lockout = lockout ?? TimeSpan.FromMinutes(5);
        _now = now ?? (() => DateTime.UtcNow);
    }

    private sealed class Entry
    {
        public readonly List<DateTime> Failures = [];
        public DateTime? LockedUntil;
    }

    /// <summary>True when the username is in its cooldown; <paramref name="retryAfter"/> is the remaining time.</summary>
    public bool IsLockedOut(string username, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_entries.TryGetValue(username, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            if (entry.LockedUntil is { } until && _now() < until)
            {
                retryAfter = until - _now();
                return true;
            }

            return false;
        }
    }

    /// <summary>Records a failed attempt, locking the username out once the threshold is hit within the window.</summary>
    public void RecordFailure(string username)
    {
        var entry = _entries.GetOrAdd(username, _ => new Entry());
        lock (entry)
        {
            var now = _now();
            entry.Failures.RemoveAll(t => now - t > _window);
            entry.Failures.Add(now);
            if (entry.Failures.Count >= _maxFailures)
            {
                entry.LockedUntil = now + _lockout;
                entry.Failures.Clear();
            }
        }
    }

    /// <summary>Clears all failure/lockout state for a username after a successful sign-in.</summary>
    public void RecordSuccess(string username) => _entries.TryRemove(username, out _);
}
