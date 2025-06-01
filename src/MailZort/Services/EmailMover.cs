namespace MailZort.Services;

// Simplified EmailMover that uses the queue service
public interface IEmailMover
{
    Task<List<EmailMoveOperation>> ExecuteTriggersAsync(List<RuleTrigger> triggers);
}

public class EmailMover : IEmailMover
{
    private readonly ILogger<EmailMover> _logger;

    public EmailMover(ILogger<EmailMover> logger)
    {
        _logger = logger;

    }

    public async Task<List<EmailMoveOperation>> ExecuteTriggersAsync(List<RuleTrigger> triggers)
    {
        List<EmailMoveOperation> ops = new List<EmailMoveOperation>();
        if (!triggers.Any())
        {
            _logger.LogDebug("No triggers to execute");
            return ops;
        }

        var groupedTriggers = GroupTriggersByFolder(triggers);
        var queuedOperations = 0;

        foreach (var (sourceFolder, moveTos) in groupedTriggers)
        {
            foreach (var moveTo in moveTos)
            {
                if (moveTo.Emails.Any())
                {
                    var moveOperation = new EmailMoveOperation
                    {
                        SourceFolder = sourceFolder,
                        DestinationFolder = moveTo.Folder,
                        Emails = moveTo.Emails
                    };

                    ops.Add(moveOperation);
                    queuedOperations++;
                }
            }
        }

        _logger.LogInformation("📋 Queued {OperationCount} move operations for {EmailCount} emails",
            queuedOperations, triggers.Count);

        return ops;
    }

    private Dictionary<string, List<MoveTo>> GroupTriggersByFolder(List<RuleTrigger> triggers)
    {
        var grouped = new Dictionary<string, List<MoveTo>>();

        foreach (var trigger in triggers)
        {
            if (!grouped.TryGetValue(trigger.From, out var moveTos))
            {
                moveTos = new List<MoveTo>();
                grouped[trigger.From] = moveTos;
            }

            var moveTo = moveTos.FirstOrDefault(x =>
                string.Equals(x.Folder, trigger.To, StringComparison.CurrentCultureIgnoreCase));

            if (moveTo == null)
            {
                moveTo = new MoveTo { Folder = trigger.To };
                moveTos.Add(moveTo);
            }

            moveTo.Emails.Add(trigger.Email);
        }

        return grouped;
    }
}

public class MoveTo
{
    public string Folder { get; set; } = string.Empty;
    public List<Email> Emails { get; set; } = new();
}
public class EmailMoveOperation
{
    public string SourceFolder { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;
    public List<Email> Emails { get; set; } = new();
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}

// Event args for move operations
public class EmailMovedEventArgs : EventArgs
{
    public string SourceFolder { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;
    public int EmailCount { get; set; }
    public List<Email> Emails { get; set; } = new();
}
