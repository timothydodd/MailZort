namespace MailZort.Services;

// Simplified EmailMover that uses the queue service
public interface IEmailMover
{
    Task ExecuteTriggersAsync(List<RuleTrigger> triggers);
}

public class EmailMover : IEmailMover
{
    private readonly ILogger<EmailMover> _logger;
    private readonly IEmailMoveQueue _moveQueue;

    public EmailMover(ILogger<EmailMover> logger, IEmailMoveQueue moveQueue)
    {
        _logger = logger;
        _moveQueue = moveQueue;
    }

    public async Task ExecuteTriggersAsync(List<RuleTrigger> triggers)
    {
        if (!triggers.Any())
        {
            _logger.LogDebug("No triggers to execute");
            return;
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

                    _moveQueue.QueueMoveOperation(moveOperation);
                    queuedOperations++;
                }
            }
        }

        _logger.LogInformation("📋 Queued {OperationCount} move operations for {EmailCount} emails",
            queuedOperations, triggers.Count);

        // Optional: Wait a bit to ensure the monitoring service processes the queue
        await Task.Delay(100);
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
