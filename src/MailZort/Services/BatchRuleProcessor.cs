using System.Collections.Concurrent;

namespace MailZort.Services;
public interface IBatchRuleProcessor
{
    Task<List<RuleTrigger>> ProcessEmailBatchAsync(List<EmailReceivedEventArgs> emails);
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

    public async Task<List<RuleTrigger>> ProcessEmailBatchAsync(List<EmailReceivedEventArgs> emails)
    {
        if (!emails.Any())
        {
            return new List<RuleTrigger>();
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var triggers = new List<RuleTrigger>();
        var enabledRules = _rules.Where(r => r.IsEnabled && r.Values?.Any() == true).ToList();

        _logger.LogInformation("Processing batch of {EmailCount} emails against {RuleCount} rules",
            emails.Count, enabledRules.Count);

        // Process all emails in parallel for better performance
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        var concurrentTriggers = new ConcurrentBag<RuleTrigger>();

        await Task.Run(() =>
        {
            Parallel.ForEach(emails, parallelOptions, email =>
            {
                var emailTriggers = ProcessSingleEmailAgainstRules(email, enabledRules);
                foreach (var trigger in emailTriggers)
                {
                    concurrentTriggers.Add(trigger);
                }
            });
        });

        triggers.AddRange(concurrentTriggers);

        stopwatch.Stop();
        _logger.LogInformation("Batch processing completed in {ElapsedMs}ms. Found {TriggerCount} rule matches",
            stopwatch.ElapsedMilliseconds, triggers.Count);

        return triggers;
    }

    private List<RuleTrigger> ProcessSingleEmailAgainstRules(EmailReceivedEventArgs email, List<Rule> rules)
    {
        var triggers = new List<RuleTrigger>();

        foreach (var rule in rules)
        {
            try
            {
                if (_ruleMatcher.CheckRuleMatch(rule, email))
                {
                    triggers.Add(CreateTrigger(rule, email));
                    // For now, only match one rule per email to avoid conflicts
                    // You can remove this break if you want multiple rules to apply
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
            To = rule.MoveTo,
            Email = new Email
            {
                MessageIndex = (int)email.UniqueId.Id,
                Folder = email.Folder,
                MoveTo = $"{rule.Name}->{rule.MoveTo}",
                Subject = email.Subject,
                SenderName = email.SenderName,
                SenderEmailaddress = email.SenderAddress,
                Date = email.ReceivedDate,
                Body = email.Body
            }
        };
    }
}
