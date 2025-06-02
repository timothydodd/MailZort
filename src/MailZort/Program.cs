// See https://aka.ms/new-console-template for more information


using MailKit;
using MailZort;
using MailZort.Services;
using ServiceStack;

internal class Program
{
    static async Task Main(string[] args)
    {
        SettingsHelper settingHelper = new();
        IConfigurationBuilder builder = new ConfigurationBuilder()
             .SetBasePath(settingHelper.SettingPath)
          .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
          .AddEnvironmentVariables();

        builder = builder.AddUserSecrets<Program>();

        IConfiguration configuration = builder.Build();

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                // Use the configuration we already built
                config.AddConfiguration(configuration);
            })
            .ConfigureServices((context, services) =>
            {
                var rules = context.Configuration.GetSection("Rules")?.Get<IEnumerable<Rule>>()?.Where(x => x.IsEnabled).ToList();

                if (rules.Any())
                {
                    services.AddSingleton(rules);
                }
                // Bind email configuration from appsettings.json
                var emailConfig = new EmailSettings();
                context.Configuration.GetSection("EmailSettings").Bind(emailConfig);
                services.AddSingleton(emailConfig);
                services.AddSingleton<MailDb>();
                services.AddSingleton<IBatchRuleProcessor, BatchRuleProcessor>();
                services.AddSingleton<IEmailMover, EmailMover>();

                services.AddSingleton<RuleMatcher>();
                services.AddSingleton(x => settingHelper);
                // Register the background service
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
}
public class EmailSettings
{
    public string? Trash { get; set; }
    public string? Server { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int Port { get; set; }
    public bool UseSsl { get; set; }
    public bool StoreMovedMessages { get; set; } = true;
    public int BatchProcessingIntervalSeconds { get; set; } = 60; // Default to 60 seconds
}
public class RuleTrigger
{
    public required string From { get; set; }
    public required string To { get; set; }
    public required UniqueId Id { get; set; }
    public required Email Email { get; set; }
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
    DoesNotMatchRegex
}
public class Stats
{
    private readonly ILogger<Stats> _logger;

    public int NewFiles { get; set; }
    public int FilesUpdated { get; set; }
    public int FilesDeleted { get; set; }
    public Stats(ILogger<Stats> logger)
    {
        _logger = logger;
    }


    public void PrintStats()
    {
        _logger.LogInformation($"New Files: {NewFiles}");
        _logger.LogInformation($"Updated Files: {FilesUpdated}");
        _logger.LogInformation($"Deleted Files: {FilesDeleted}");
    }
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

public class ProcessingProgressEventArgs : EventArgs
{
    public int CurrentIndex { get; set; }
    public int TotalCount { get; set; }
    public bool IsComplete { get; set; }
    public double PercentComplete => TotalCount > 0 ? (double)CurrentIndex / TotalCount * 100 : 0;
}
public class BatchProcessingEventArgs : EventArgs
{
    public int EmailsProcessed { get; set; }
    public int RulesMatched { get; set; }
    public int EmailsMoved { get; set; }
    public TimeSpan ProcessingTime { get; set; }
}
