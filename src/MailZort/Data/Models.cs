using System.ComponentModel.DataAnnotations.Schema;
using RoboDodd.OrmLite;

namespace MailZort.Data;

/// <summary>A sorting rule. Migrated out of appsettings.json on first run; edited in the portal.</summary>
[Table("Rules")]
public class DbRule
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Folder { get; set; } = "Inbox";
    public string? MoveTo { get; set; }
    public int Action { get; set; }
    public int LookIn { get; set; }
    public int ExpressionType { get; set; }
    public bool IsOr { get; set; } = true;
    public int DaysOld { get; set; }
    public bool? RequireUnread { get; set; }
    public bool? RequireNotImportant { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    /// <summary>Running total of emails this rule has moved, so dead rules are obvious in the portal.</summary>
    public int MatchCount { get; set; }
    public DateTime? LastMatchedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

[Table("RuleValues")]
public class DbRuleValue
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    [Index]
    public int RuleId { get; set; }
    public string Value { get; set; } = string.Empty;
}

[Table("Senders")]
public class DbSender
{
    [PrimaryKey]
    public string Address { get; set; } = string.Empty;
    [Index]
    public int Status { get; set; }
    public int Rescues { get; set; }
    public int SpamMoves { get; set; }
    public string? LastSubject { get; set; }
    public string? Note { get; set; }
    /// <summary>"observed" when MailZort worked it out, otherwise the portal user who set it.</summary>
    public string? SetBy { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Where a watched message was on the last observation pass. Drives move detection.</summary>
[Table("MessageLocations")]
public class DbMessageLocation
{
    [PrimaryKey]
    public string MessageKey { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    /// <summary>True when MailZort made the move, so the next transition is not counted as a user's judgement.</summary>
    public bool Suppress { get; set; }
    public DateTime SeenUtc { get; set; }
}

[Table("Activity")]
public class DbActivity
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    [Index]
    public DateTime CreatedUtc { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? MessageKey { get; set; }
    public string? Subject { get; set; }
    public string? SenderName { get; set; }
    [Index]
    public string? SenderAddress { get; set; }
    public string? Recipient { get; set; }
    public string? FromFolder { get; set; }
    public string? ToFolder { get; set; }
    public string? RuleName { get; set; }
    public string? Detail { get; set; }
    /// <summary>Set when the message can still be put back where it came from.</summary>
    public bool CanUndo { get; set; }
    public DateTime? UndoneUtc { get; set; }
}

public static class ActivityType
{
    public const string Moved = "Moved";
    public const string Flagged = "Flagged";
    public const string Purged = "Purged";
    public const string Rescued = "Rescued";
    public const string Trusted = "Trusted";
    public const string Blacklisted = "Blacklisted";
    public const string TrustRevoked = "TrustRevoked";
    public const string Restored = "Restored";
    public const string Error = "Error";
}

/// <summary>Work the portal asked the mail loop to do. Persisted so a restart cannot lose it.</summary>
[Table("PendingActions")]
public class DbPendingAction
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string ActionType { get; set; } = PendingActionType.MoveMessage;
    public string? MessageKey { get; set; }
    public string SourceFolder { get; set; } = string.Empty;
    public string TargetFolder { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string? SenderAddress { get; set; }
    public int? ActivityId { get; set; }
    public string? RequestedBy { get; set; }
    [Index]
    public string Status { get; set; } = PendingActionStatus.Pending;
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public static class PendingActionType
{
    public const string MoveMessage = "MoveMessage";
}

public static class PendingActionStatus
{
    public const string Pending = "Pending";
    public const string Done = "Done";
    public const string Failed = "Failed";
}

[Table("Settings")]
public class DbSetting
{
    [PrimaryKey]
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

[Table("Users")]
public class DbUser
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    [Index(IsUnique = true)]
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
}
