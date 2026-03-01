namespace MailZort.Services;
public interface IBatchRuleProcessor
{
    List<RuleTrigger> ProcessEmailBatch(List<EmailReceivedEventArgs> emails);
}
public class BatchRuleProcessor : IBatchRuleProcessor
{
    private readonly ILogger<BatchRuleProcessor> _logger;
    private readonly IEnumerable<Rule> _rules;
    private readonly RuleMatcher _ruleMatcher;

    public BatchRuleProcessor(ILogger<BatchRuleProcessor> logger, List<Rule> rules, RuleMatcher ruleMatcher)
    {
        _logger = logger;
        _rules = rules;
        _ruleMatcher = ruleMatcher;
    }

    public List<RuleTrigger> ProcessEmailBatch(List<EmailReceivedEventArgs> emails)
    {
        if (!emails.Any())
        {
            return new List<RuleTrigger>();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var triggers = new List<RuleTrigger>();
        var enabledRules = _rules.Where(r => r.IsEnabled && (r.ExpressionType == ExpressionType.AllEmails || r.Values?.Any() == true)).ToList();

        _logger.LogInformation("Processing batch of {EmailCount} emails against {RuleCount} rules",
            emails.Count, enabledRules.Count);

        foreach (var email in emails)
        {
            var emailTriggers = ProcessSingleEmailAgainstRules(email, enabledRules);
            triggers.AddRange(emailTriggers);
        }

        stopwatch.Stop();
        _logger.LogInformation("Batch processing completed in {ElapsedMs}ms. Found {TriggerCount} rule matches",
            stopwatch.ElapsedMilliseconds, triggers.Count);

        return triggers;
    }

    private List<RuleTrigger> ProcessSingleEmailAgainstRules(EmailReceivedEventArgs email, List<Rule> rules)
    {
        var triggers = new List<RuleTrigger>();

        // Skip important/flagged emails from rule processing
        if (email.IsImportant)
            return triggers;

        foreach (var rule in rules)
        {
            try
            {
                if (_ruleMatcher.CheckRuleMatch(rule, email))
                {
                    triggers.Add(CreateTrigger(rule, email));
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing rule {RuleName} for email {Subject}",
                    rule.Name, email.Subject);
            }
        }

        return triggers;
    }

    private static RuleTrigger CreateTrigger(Rule rule, EmailReceivedEventArgs email)
    {
        return new RuleTrigger
        {
            Id = email.UniqueId,
            From = email.Folder,
            To = rule.Action == RuleAction.MarkImportant ? string.Empty : rule.MoveTo ?? string.Empty,
            Action = rule.Action,
            Email = new Email
            {
                MessageIndex = (int)email.UniqueId.Id,
                Folder = email.Folder,
                MoveTo = rule.Action == RuleAction.MarkImportant ? $"{rule.Name}->Flagged" : $"{rule.Name}->{rule.MoveTo}",
                Subject = email.Subject,
                SenderName = email.SenderName,
                SenderEmailaddress = email.SenderAddress,
                Date = email.ReceivedDate,
                Body = email.Body
            }
        };
    }
}
