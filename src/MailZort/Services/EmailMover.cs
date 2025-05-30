using System.Data;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using ServiceStack.OrmLite;

namespace MailZort.Services;

public interface IEmailMover
{
    Task ExecuteTriggersAsync(List<RuleTrigger> triggers);
}

public class EmailMover : IEmailMover
{
    private readonly ILogger<EmailMover> _logger;
    private readonly MailDb _mailDb;
    private readonly EmailSettings _config;

    public EmailMover(ILogger<EmailMover> logger, MailDb mailDb, EmailSettings config)
    {
        _logger = logger;
        _mailDb = mailDb;
        _config = config;
    }

    public async Task ExecuteTriggersAsync(List<RuleTrigger> triggers)
    {
        if (!triggers.Any())
        {
            _logger.LogDebug("No triggers to execute");
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var groupedTriggers = GroupTriggersByFolder(triggers);

        using var mailClient = new ImapClient();
        using var dbConnection = _mailDb.GetConnection();

        try
        {
            await ConnectToImapAsync(mailClient);
            await ProcessGroupedTriggersAsync(mailClient, dbConnection, groupedTriggers);

            stopwatch.Stop();
            _logger.LogInformation("✅ Moved {TriggerCount} emails in {ElapsedMs}ms",
                triggers.Count, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing email triggers");
            throw;
        }
        finally
        {
            await DisconnectImapAsync(mailClient);
        }
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

    private async Task ConnectToImapAsync(ImapClient mailClient)
    {
        await mailClient.ConnectAsync(_config.Server, _config.Port, SecureSocketOptions.SslOnConnect);
        await mailClient.AuthenticateAsync(_config.Username, _config.Password);
    }

    private async Task ProcessGroupedTriggersAsync(
        ImapClient mailClient,
        IDbConnection dbConnection,
        Dictionary<string, List<MoveTo>> groupedTriggers)
    {
        foreach (var (folderName, moveTos) in groupedTriggers)
        {
            await ProcessFolderAsync(mailClient, dbConnection, folderName, moveTos);
        }
    }

    private async Task ProcessFolderAsync(
        ImapClient mailClient,
        IDbConnection dbConnection,
        string folderName,
        List<MoveTo> moveTos)
    {
        try
        {
            var sourceFolder = mailClient.GetFolder(folderName);
            await sourceFolder.OpenAsync(FolderAccess.ReadWrite);

            foreach (var moveTo in moveTos)
            {
                await ProcessMoveOperationAsync(mailClient, dbConnection, sourceFolder, moveTo);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing folder {FolderName}", folderName);
            throw;
        }
    }

    private async Task ProcessMoveOperationAsync(
        ImapClient mailClient,
        IDbConnection dbConnection,
        IMailFolder sourceFolder,
        MoveTo moveTo)
    {
        if (!moveTo.Emails.Any())
        {
            return;
        }

        try
        {
            var destinationFolder = GetDestinationFolder(mailClient, moveTo.Folder);
            var indexes = moveTo.Emails.Select(e => e.MessageIndex).ToList();

            await sourceFolder.MoveToAsync(indexes, destinationFolder);
            SaveEmailsToDatabase(dbConnection, moveTo.Emails);

            _logger.LogInformation("📁 Moved {Count} emails to {Folder}",
                moveTo.Emails.Count, moveTo.Folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving emails to folder {Folder}", moveTo.Folder);
            throw;
        }
    }

    private IMailFolder GetDestinationFolder(ImapClient mailClient, string folderName)
    {
        if (string.Equals(folderName, "trash", StringComparison.CurrentCultureIgnoreCase))
        {
            return mailClient.Capabilities.HasFlag(ImapCapabilities.SpecialUse)
                ? mailClient.GetFolder(SpecialFolder.Trash)
                : mailClient.GetFolder(_config.Trash);
        }

        return mailClient.GetFolder(folderName);
    }

    private void SaveEmailsToDatabase(IDbConnection dbConnection, List<Email> emails)
    {
        foreach (var email in emails)
        {
            try
            {
                dbConnection.Insert(email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving email {MessageIndex} to database", email.MessageIndex);
            }
        }
    }

    private async Task DisconnectImapAsync(ImapClient mailClient)
    {
        if (mailClient.IsConnected)
        {
            await mailClient.DisconnectAsync(true);
        }
    }
}
public class MoveTo
{
    public string Folder { get; set; } = string.Empty;
    public List<Email> Emails { get; set; } = new();
}
