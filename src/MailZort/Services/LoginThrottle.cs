using System.Collections.Concurrent;

namespace MailZort.Services;

/// <summary>
/// Slows down password guessing. The portal ships with a known default credential and is exposed
/// on the LAN over plain HTTP, so an unthrottled login form is the weakest thing here.
/// </summary>
public class LoginThrottle
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Attempts> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLockedOut(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_attempts.TryGetValue(key, out var state))
            return false;

        lock (state)
        {
            if (state.LockedUntilUtc is { } until && until > DateTime.UtcNow)
            {
                retryAfter = until - DateTime.UtcNow;
                return true;
            }
        }

        return false;
    }

    public void RecordFailure(string key)
    {
        var state = _attempts.GetOrAdd(key, _ => new Attempts());

        lock (state)
        {
            // Failures older than the window do not count towards a lockout.
            if (DateTime.UtcNow - state.FirstFailureUtc > Window)
            {
                state.Count = 0;
                state.FirstFailureUtc = DateTime.UtcNow;
            }

            state.Count++;
            if (state.Count >= MaxAttempts)
            {
                state.LockedUntilUtc = DateTime.UtcNow.Add(Lockout);
                state.Count = 0;
                state.FirstFailureUtc = DateTime.UtcNow;
            }
        }
    }

    public void RecordSuccess(string key) => _attempts.TryRemove(key, out _);

    private sealed class Attempts
    {
        public int Count;
        public DateTime FirstFailureUtc = DateTime.UtcNow;
        public DateTime? LockedUntilUtc;
    }
}
