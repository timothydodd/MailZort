namespace MailZort.Services;

public enum MailServiceState
{
    Starting,
    Connecting,
    Idle,
    Processing,
    Reconnecting,
    Stopped
}

/// <summary>
/// Live state of the mail loop, published for the portal. The loop writes, the dashboard reads -
/// deliberately a plain shared object rather than anything that could block mail processing.
/// </summary>
public class MailServiceStatus
{
    private readonly object _sync = new();

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public MailServiceState State { get; private set; } = MailServiceState.Starting;
    public string? Activity { get; private set; }
    public bool Connected { get; private set; }
    public string? Server { get; private set; }
    public string? Mailbox { get; private set; }
    public bool SupportsIdle { get; private set; }

    public DateTime? ConnectedSinceUtc { get; private set; }
    public DateTime? LastFullPassUtc { get; private set; }
    public DateTime? NextFullPassUtc { get; private set; }
    public DateTime? LastPassEndedUtc { get; private set; }
    public TimeSpan? LastPassDuration { get; private set; }
    public int LastPassEmailCount { get; private set; }
    public int LastPassMatchCount { get; private set; }
    public int InboxCount { get; private set; }

    public string? LastError { get; private set; }
    public DateTime? LastErrorUtc { get; private set; }
    public DateTime? NextRetryUtc { get; private set; }

    public TimeSpan Uptime => DateTime.UtcNow - StartedUtc;

    /// <summary>Raised whenever the state changes, so open dashboards can refresh.</summary>
    public event Action? Changed;

    public void SetConnecting(string? server, string? mailbox)
    {
        lock (_sync)
        {
            State = MailServiceState.Connecting;
            Server = server;
            Mailbox = mailbox;
            Connected = false;
            Activity = "Connecting to the mail server";
            NextRetryUtc = null;
        }
        Changed?.Invoke();
    }

    public void SetConnected(bool supportsIdle)
    {
        lock (_sync)
        {
            State = MailServiceState.Idle;
            Connected = true;
            SupportsIdle = supportsIdle;
            ConnectedSinceUtc = DateTime.UtcNow;
            Activity = supportsIdle ? "Waiting for new mail (IDLE)" : "Polling for new mail";
            LastError = null;
        }
        Changed?.Invoke();
    }

    public void SetIdle()
    {
        lock (_sync)
        {
            if (!Connected)
                return;
            State = MailServiceState.Idle;
            Activity = SupportsIdle ? "Waiting for new mail (IDLE)" : "Polling for new mail";
        }
        Changed?.Invoke();
    }

    public void SetProcessing(string what)
    {
        lock (_sync)
        {
            State = MailServiceState.Processing;
            Activity = what;
        }
        Changed?.Invoke();
    }

    public void RecordPass(bool fullPass, int emails, int matches, TimeSpan duration, int inboxCount,
        TimeSpan fullPassInterval)
    {
        lock (_sync)
        {
            LastPassEndedUtc = DateTime.UtcNow;
            LastPassDuration = duration;
            LastPassEmailCount = emails;
            LastPassMatchCount = matches;
            InboxCount = inboxCount;

            if (fullPass)
            {
                LastFullPassUtc = DateTime.UtcNow;
                NextFullPassUtc = DateTime.UtcNow.Add(fullPassInterval);
            }
        }
        Changed?.Invoke();
    }

    public void SetDisconnected(string? error, TimeSpan? retryIn)
    {
        lock (_sync)
        {
            State = error == null ? MailServiceState.Stopped : MailServiceState.Reconnecting;
            Connected = false;
            ConnectedSinceUtc = null;
            Activity = error == null ? "Stopped" : "Waiting to reconnect";
            if (error != null)
            {
                LastError = error;
                LastErrorUtc = DateTime.UtcNow;
            }
            NextRetryUtc = retryIn.HasValue ? DateTime.UtcNow.Add(retryIn.Value) : null;
        }
        Changed?.Invoke();
    }
}
