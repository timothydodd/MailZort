// See https://aka.ms/new-console-template for more information

using System.Data;
using System.Reflection;
using MailZort;
using MailZort.Services;
using ServiceStack;
using ServiceStack.DataAnnotations;
using ServiceStack.OrmLite;

internal class Program
{
    private static ServiceProvider? _serviceProvider;

    private static IConfigurationRoot? _configuration;

    private static void Configure()
    {
        SettingsHelper settingHelper = new();

        IConfigurationBuilder builder = new ConfigurationBuilder()
                      .SetBasePath(settingHelper.SettingPath)
                      .AddJsonFile("appsettings.json", true, true)
                      .AddEnvironmentVariables();



        builder = builder.AddUserSecrets<Program>();
        _configuration = builder.Build();
        IServiceCollection services = new ServiceCollection()
      .AddSingleton<IServiceProvider>(c => _serviceProvider);

        services.AddLogging(configure => configure.AddSimpleConsole(options =>
         {
             options.IncludeScopes = false;
             options.SingleLine = true;
             options.TimestampFormat = "hh:mm:ss ";
         }));
        services.AddSingleton(x => _configuration.GetSection("EmailSettings").Get<EmailSettings>());

        services.AddSingleton(x => settingHelper);
        services.AddSingleton<MailProcessor>();
        services.AddSingleton<MailClient>();
        services.AddSingleton<MailDb>();


        _serviceProvider = services.BuildServiceProvider();

    }
    private static void Main(string[] args)
    {
        Configure();
        if (_serviceProvider is null)
        {
            throw new Exception("Service Provider is null");
        }
        if (_configuration is null)
        {
            throw new Exception("Configuration is null");
        }
        Version? AssemblyVersion = Assembly.GetExecutingAssembly()?.GetName()?.Version;

        ILogger<Program> logger = _serviceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogInformation($"MailZort Version: {AssemblyVersion}");


        MailProcessor mailProcessor = _serviceProvider.GetRequiredService<MailProcessor>();
        EmailSettings mailSettings = _serviceProvider.GetRequiredService<EmailSettings>();

        var rules = _configuration.GetSection("Rules")?.Get<IEnumerable<Rule>>()?.Where(x => x.IsEnabled)?.ToList();
        logger.LogInformation($"Rules Loaded: {rules?.Count}");
        if (mailSettings.PullMails)
        {
            MailClient mailClientService = _serviceProvider.GetRequiredService<MailClient>();
            mailClientService.GetMessages(rules);


        }
        List<RuleTrigger>? triggers = rules == null ? new List<RuleTrigger>() : mailProcessor.RunRules(rules);
        if (!mailSettings.TestMode)
        {
            logger.LogInformation($"Emails Found for Filtering {triggers.Count}");
            mailProcessor.RunTriggers(triggers);
        }


    }
}

public class Email
{
    [Index]
    public string Folder { get; set; }
    [Index]
    public string SenderName { get; set; }
    public string SenderEmailaddress { get; set; }
    [Index]
    public uint Id { get; set; }
    [Index]
    public DateTimeOffset Date { get; set; }
    public string Subject { get; set; }
    public string TextBody { get; set; }
    public string HtmlBody { get; set; }

    public Email(string senderName, string senderEmailaddress, uint id, string subject, string body,
        string htmlBody, string folder, DateTimeOffset date)
    {
        SenderName = senderName;
        SenderEmailaddress = senderEmailaddress;
        Id = id;
        Subject = subject;
        TextBody = body;
        HtmlBody = htmlBody;
        Folder = folder;
        Date = date;
    }

    public override string ToString()
    {
        return $"{nameof(SenderName)}: {SenderName}, {nameof(SenderEmailaddress)}: {SenderEmailaddress}, {nameof(Id)}: {Id}, {nameof(Subject)}: {Subject}";
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
    public bool PullMails { get; set; }
    public bool TestMode { get; set; } = true;
    public string? SearchMode { get; set; }
    public string? Server { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int Port { get; set; }
    public bool UseSsl { get; set; }
}
public class RuleTrigger
{
    public string? From { get; set; }
    public string? To { get; set; }
    public uint? Id { get; set; }
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
