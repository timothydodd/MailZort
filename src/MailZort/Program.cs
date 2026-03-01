using MailKit;
using MailZort.Services;

internal class Program
{
    static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                config.Sources.Clear();
                config.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false);
                config.AddEnvironmentVariables();
                config.AddUserSecrets<Program>();
            })
            .ConfigureServices((context, services) =>
            {
                var rules = context.Configuration.GetSection("Rules").Get<List<Rule>>()
                    ?.Where(x => x.IsEnabled).ToList();

                PrintRules(rules);

                if (rules != null && rules.Any())
                {
                    services.AddSingleton(rules);
                }

                var emailConfig = new EmailSettings();
                context.Configuration.GetSection("EmailSettings").Bind(emailConfig);

                if (string.IsNullOrWhiteSpace(emailConfig.Server) ||
                    string.IsNullOrWhiteSpace(emailConfig.Username) ||
                    string.IsNullOrWhiteSpace(emailConfig.Password))
                {
                    throw new InvalidOperationException("EmailSettings: Server, Username, and Password are required.");
                }

                services.AddSingleton(emailConfig);
                services.AddSingleton<IBatchRuleProcessor, BatchRuleProcessor>();
                services.AddSingleton<IEmailMover, EmailMover>();
                services.AddSingleton<RuleMatcher>();
                services.AddHostedService<EmailMonitoringService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        Console.WriteLine("Starting email monitoring service...");
        Console.WriteLine("Press Ctrl+C to stop the service.");

        await host.RunAsync();
    }

    private static void PrintRules(List<Rule>? rules)
    {
        Console.WriteLine();
        Console.WriteLine("=== Rules Configuration ===");

        if (rules == null || !rules.Any())
        {
            Console.WriteLine("  WARNING: No rules loaded!");
            Console.WriteLine("===========================");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"  Total rules: {rules.Count}");
        Console.WriteLine();

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var action = rule.Action == RuleAction.MarkImportant ? "Flag" : $"Move -> {rule.MoveTo}";
            var values = rule.Values != null ? string.Join(", ", rule.Values) : "none";
            var filters = new List<string>();
            if (rule.DaysOld > 0) filters.Add($"DaysOld>={rule.DaysOld}");
            if (rule.RequireUnread == true) filters.Add("UnreadOnly");
            if (rule.RequireNotImportant == true) filters.Add("NotImportant");

            Console.WriteLine($"  [{i + 1}] {rule.Name}");
            Console.WriteLine($"      {rule.Folder} -> {action}");
            Console.WriteLine($"      Match: {rule.ExpressionType} in {rule.LookIn}");
            Console.WriteLine($"      Values: [{values}]");
            if (filters.Any())
                Console.WriteLine($"      Filters: {string.Join(", ", filters)}");
            Console.WriteLine();
        }

        Console.WriteLine("===========================");
        Console.WriteLine();
    }
}

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
}
public class RuleTrigger
{
    public required string From { get; set; }
    public required string To { get; set; }
    public required UniqueId Id { get; set; }
    public required Email Email { get; set; }
    public RuleAction Action { get; set; } = RuleAction.Move;
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
    public DateTime ReceivedDate { get; set; }
    public string Folder { get; set; } = string.Empty;
    public bool IsRead { get; set; } = false;
    public bool IsImportant { get; set; } = false;
    public required UniqueId UniqueId { get; set; }
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
    public string? SenderName { get; set; }
    public string? SenderEmailaddress { get; set; }
    public DateTimeOffset Date { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
}

public class EmailFlagOperation
{
    public string SourceFolder { get; set; } = string.Empty;
    public List<UniqueId> EmailIds { get; set; } = new();
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}
