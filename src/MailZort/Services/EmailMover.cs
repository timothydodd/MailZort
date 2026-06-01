
using MailKit;

namespace MailZort.Services;

// Simplified EmailMover that uses the queue service
public interface IEmailMover
{
    List<EmailMoveOperation> ExecuteTriggers(List<RuleTrigger> triggers);
    List<EmailFlagOperation> ExtractFlagOperations(List<RuleTrigger> triggers);
}

public class EmailMover : IEmailMover
{
    private readonly ILogger<EmailMover> _logger;

    public EmailMover(ILogger<EmailMover> logger)
    {
        _logger = logger;

    }

    public List<EmailFlagOperation> ExtractFlagOperations(List<RuleTrigger> triggers)
    {
        var flagTriggers = triggers.Where(t => t.Action == RuleAction.MarkImportant).ToList();
        if (!flagTriggers.Any())
            return new List<EmailFlagOperation>();

        var grouped = flagTriggers
            .GroupBy(t => t.From)
            .Select(g => new EmailFlagOperation
            {
                SourceFolder = g.Key,
                EmailIds = g.Select(t => t.Id).ToList()
            })
            .ToList();

        _logger.LogDebug("Created {OperationCount} flag operations for {EmailCount} emails",
            grouped.Count, flagTriggers.Count);

        return grouped;
    }

    public List<EmailMoveOperation> ExecuteTriggers(List<RuleTrigger> triggers)
    {
        List<EmailMoveOperation> ops = new List<EmailMoveOperation>();
        if (!triggers.Any())
        {
            _logger.LogDebug("No triggers to execute");
            return ops;
        }

        // Only process Move triggers for move operations
        triggers = triggers.Where(t => t.Action == RuleAction.Move).ToList();
        if (!triggers.Any())
            return ops;

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
                        Emails = moveTo.Emails,
                        EmailIds = moveTo.EmailIds

                    };

                    ops.Add(moveOperation);
                    queuedOperations++;
                }
            }
        }

        _logger.LogDebug("Queued {OperationCount} move operations for {EmailCount} emails",
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
                string.Equals(x.Folder, trigger.To, StringComparison.OrdinalIgnoreCase));

            if (moveTo == null)
            {
                moveTo = new MoveTo { Folder = trigger.To };
                moveTos.Add(moveTo);
            }
            moveTo.EmailIds.Add(trigger.Id);
            moveTo.Emails.Add(trigger.Email);
        }

        return grouped;
    }

}

public class MoveTo
{
    public string Folder { get; set; } = string.Empty;
    public List<UniqueId> EmailIds { get; set; } = new();
    public List<Email> Emails { get; set; } = new();
}
public class EmailMoveOperation
{
    public string SourceFolder { get; set; } = string.Empty;
    public string DestinationFolder { get; set; } = string.Empty;
    public List<Email> Emails { get; set; } = new();
    public List<UniqueId> EmailIds { get; set; } = new();
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}
