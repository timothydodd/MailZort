using MailZort.Data;

namespace MailZort.Services;

/// <summary>
/// The verbs behind the portal's buttons. Anything touching the mailbox is queued for the mail
/// loop rather than done here - the portal has no IMAP connection of its own.
/// </summary>
public class PortalActions
{
    private readonly ILogger<PortalActions> _logger;
    private readonly IActivityStore _activity;
    private readonly ISenderStore _senders;
    private readonly IPendingActionStore _pendingActions;

    public PortalActions(ILogger<PortalActions> logger, IActivityStore activity, ISenderStore senders,
        IPendingActionStore pendingActions)
    {
        _logger = logger;
        _activity = activity;
        _senders = senders;
        _pendingActions = pendingActions;
    }

    /// <summary>Puts a moved message back where it came from.</summary>
    public async Task<string> MoveBackAsync(int activityId, string user, CancellationToken cancellationToken = default)
    {
        var entry = await _activity.GetAsync(activityId, cancellationToken);
        if (entry == null)
            return "That history entry no longer exists.";

        if (string.IsNullOrWhiteSpace(entry.MessageKey) || entry.MessageKey.StartsWith('~'))
            return "No Message-Id was recorded for this email, so it cannot be found again.";

        if (string.IsNullOrWhiteSpace(entry.ToFolder) || string.IsNullOrWhiteSpace(entry.FromFolder))
            return "This entry has no source and destination to reverse.";

        await _pendingActions.EnqueueAsync(new DbPendingAction
        {
            ActionType = PendingActionType.MoveMessage,
            MessageKey = entry.MessageKey,
            SourceFolder = entry.ToFolder,
            TargetFolder = entry.FromFolder,
            Subject = entry.Subject,
            SenderAddress = entry.SenderAddress,
            ActivityId = entry.Id,
            RequestedBy = user
        }, cancellationToken);

        _logger.LogInformation("{User} asked to move '{Subject}' back from {To} to {From}",
            user, entry.Subject, entry.ToFolder, entry.FromFolder);

        return $"Queued: '{Trim(entry.Subject)}' will move back to {entry.FromFolder}.";
    }

    /// <summary>Trusts the sender and, when the email is still filed away, brings that one back too.</summary>
    public async Task<string> WhitelistAsync(int activityId, string user, CancellationToken cancellationToken = default)
    {
        var entry = await _activity.GetAsync(activityId, cancellationToken);
        if (entry == null)
            return "That history entry no longer exists.";

        if (string.IsNullOrWhiteSpace(entry.SenderAddress))
            return "No sender address was recorded for this email.";

        await _senders.SetStatusAsync(entry.SenderAddress, SenderStatus.Trusted, user, cancellationToken: cancellationToken);

        var message = $"{entry.SenderAddress} is now trusted - their mail will be left alone.";
        if (entry.CanUndo && entry.UndoneUtc == null)
        {
            message += " " + await MoveBackAsync(activityId, user, cancellationToken);
        }

        return message;
    }

    public async Task<string> BlacklistAsync(int activityId, string user, CancellationToken cancellationToken = default)
    {
        var entry = await _activity.GetAsync(activityId, cancellationToken);
        if (entry?.SenderAddress == null)
            return "No sender address was recorded for this email.";

        await _senders.SetStatusAsync(entry.SenderAddress, SenderStatus.Blacklisted, user, cancellationToken: cancellationToken);
        return $"{entry.SenderAddress} is blacklisted - their mail will go to Junk.";
    }

    public async Task<string> SetSenderStatusAsync(string address, SenderStatus status, string user,
        string? note = null, CancellationToken cancellationToken = default)
    {
        var normalized = SenderStore.Normalize(address);
        if (normalized == null)
            return $"'{address}' is not an email address.";

        await _senders.SetStatusAsync(normalized, status, user, note, cancellationToken);
        return status switch
        {
            SenderStatus.Trusted => $"{normalized} is now trusted.",
            SenderStatus.Blacklisted => $"{normalized} is now blacklisted.",
            _ => $"{normalized} reset to neutral."
        };
    }

    private static string Trim(string? value) =>
        string.IsNullOrEmpty(value) ? "(no subject)"
            : value.Length <= 50 ? value
            : value[..47] + "...";
}
