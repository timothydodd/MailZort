using MailKit;

public class Rule
{
    public bool IsEnabled { get; set; } = true;
    public string? Name { get; set; }
    public string? Folder { get; set; }
    public string? MoveTo { get; set; }
    public bool IsOr { get; set; } = true;
    public LookIn LookIn { get; set; }
    public ExpressionType ExpressionType { get; set; }
    public int DaysOld { get; set; }
    public List<string>? Values { get; set; }
    /// <summary>
    /// If true, rule only matches unread emails. If false or null, read status is ignored.
    /// </summary>
    public bool? RequireUnread { get; set; }
    /// <summary>
    /// If true, rule only matches emails that are NOT marked as important. If false or null, importance is ignored.
    /// </summary>
    public bool? RequireNotImportant { get; set; }
    public RuleAction Action { get; set; } = RuleAction.Move;
}
public class EmailSettings
{
    public string? Trash { get; set; }
    public string? Server { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int Port { get; set; }
    public bool UseSsl { get; set; }
    public int BatchProcessingIntervalSeconds { get; set; } = 60;
    public bool InboxCleanupEnabled { get; set; } = true;
    public int InboxCleanupDaysOld { get; set; } = 30;
    public string ImportantFolder { get; set; } = "Important";
    /// <summary>
    /// If true, emails dated in the future are permanently deleted (expunged, not moved to Trash)
    /// before any rules run.
    /// </summary>
    public bool PurgeFutureDatedEnabled { get; set; } = true;
    /// <summary>
    /// How far ahead of now an email's date may be before it is considered future-dated.
    /// Allows for clock skew between the sender and this machine.
    /// </summary>
    public int FutureDatedToleranceMinutes { get; set; } = 60;
    /// <summary>
    /// Junk/Spam folder name. Only used when the server does not advertise a special-use Junk folder.
    /// </summary>
    public string Junk { get; set; } = "Junk";
    /// <summary>
    /// If true, MailZort watches which folders messages move between and builds sender reputation:
    /// rescued out of Junk =&gt; trusted, repeatedly put back into Junk =&gt; blacklisted.
    /// </summary>
    public bool SenderReputationEnabled { get; set; } = true;
    /// <summary>
    /// How many times a sender's mail must be moved into Junk before they are blacklisted.
    /// </summary>
    public int BlacklistThreshold { get; set; } = 2;
    /// <summary>
    /// Only messages delivered within this many days are watched for folder moves.
    /// </summary>
    public int ReputationLookbackDays { get; set; } = 30;
    /// <summary>
    /// Extra folders to watch for moves, beyond Inbox, Junk and Trash. A message rescued from Junk
    /// into one of these counts as a vote of confidence in the sender.
    /// </summary>
    public List<string> ReputationWatchFolders { get; set; } = new();
    /// <summary>
    /// Directory for persisted state (the SQLite database). Relative paths resolve against the app directory.
    /// </summary>
    public string DataDirectory { get; set; } = "data";
    /// <summary>
    /// Optional. Leave empty for SQLite in <see cref="DataDirectory"/>; set a MySQL connection
    /// string to use MySQL instead.
    /// </summary>
    public string? ConnectionString { get; set; }
}

public enum SenderStatus
{
    Neutral,
    Trusted,
    Blacklisted
}

public class SenderReputation
{
    public string Address { get; set; } = string.Empty;
    public SenderStatus Status { get; set; } = SenderStatus.Neutral;
    /// <summary>Times this sender's mail was moved out of Junk into a real folder.</summary>
    public int Rescues { get; set; }
    /// <summary>Times this sender's mail was moved into Junk since the last rescue.</summary>
    public int SpamMoves { get; set; }
    public string? LastSubject { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastUpdatedUtc { get; set; }
}
public class RuleTrigger
{
    public required string From { get; set; }
    public required string To { get; set; }
    public required UniqueId Id { get; set; }
    public required Email Email { get; set; }
    public RuleAction Action { get; set; } = RuleAction.Move;
    public string MessageKey { get; set; } = string.Empty;
}
public enum LookIn
{
    All,
    Subject,
    Body,
    Sender,
    Recipient,
    SenderEmail
}
public enum ExpressionType
{
    Contains,
    DoesNotContain,
    Is,
    IsNot,
    StartsWith,
    EndsWith,
    MatchesRegex,
    DoesNotMatchRegex,
    AllEmails
}

public class EmailReceivedEventArgs : EventArgs
{
    public string From { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    /// <summary>To addresses, ';' joined. LookIn.Recipient matches against this.</summary>
    public string Recipients { get; set; } = string.Empty;
    /// <summary>Always UTC - the envelope date is normalised on the way in.</summary>
    public DateTime ReceivedDate { get; set; }
    public string Folder { get; set; } = string.Empty;
    public bool IsRead { get; set; } = false;
    public bool IsImportant { get; set; } = false;
    public required UniqueId UniqueId { get; set; }
    /// <summary>Stable identity used to follow a message as it moves between folders.</summary>
    public string MessageKey { get; set; } = string.Empty;
}

public class BatchProcessingEventArgs : EventArgs
{
    public int EmailsProcessed { get; set; }
    public int RulesMatched { get; set; }
    public int EmailsMoved { get; set; }
    public TimeSpan ProcessingTime { get; set; }
}

public enum RuleAction
{
    Move,
    MarkImportant
}

public class Email
{
    public int MessageIndex { get; set; }
    public string? Folder { get; set; }
    public string? MoveTo { get; set; }
    public string? Rule { get; set; }
    public string? SenderName { get; set; }
    public string? SenderEmailaddress { get; set; }
    public DateTimeOffset Date { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
    /// <summary>Message-Id, used to find the message again after it has moved folders.</summary>
    public string MessageKey { get; set; } = string.Empty;
}

public class EmailFlagOperation
{
    public string SourceFolder { get; set; } = string.Empty;
    public List<UniqueId> EmailIds { get; set; } = new();
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A permanent delete (flag as \Deleted then expunge) - the message never reaches Trash.
/// </summary>
public class EmailPurgeOperation
{
    public string SourceFolder { get; set; } = string.Empty;
    public List<UniqueId> EmailIds { get; set; } = new();
    public List<PurgedEmail> Emails { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

public class PurgedEmail
{
    public string From { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
}
