using System.Data;
using System.Diagnostics;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using ServiceStack.OrmLite;


namespace MailZort.Services;

internal class MailProcessor
{
    private readonly EmailSettings mailSettings;
    private readonly MailDb _mailDb;
    private readonly MailClient _mailClientService;
    private readonly ILogger<MailProcessor> _logger;

    public MailProcessor(
        EmailSettings mailSettings,
        MailDb mailDb,
        MailClient mailClientService, ILogger<MailProcessor> logger)
    {
        this.mailSettings = mailSettings;
        _mailDb = mailDb;
        _mailClientService = mailClientService;
        _logger = logger;
    }
    public List<RuleTrigger> RunRules(IEnumerable<Rule> rules)
    {
        using IDbConnection dbc = _mailDb.GetConnection();
        List<RuleTrigger> triggers = new();

        Dictionary<string, int> matchesFound = new();

        foreach (Rule rule in rules)
        {
            List<Email> emails = dbc.Select<Email>().Where(x => x.Folder.Equals(rule.Folder, StringComparison.CurrentCultureIgnoreCase)).ToList();
            _logger.LogInformation($"Running Rule {rule.Name} on {emails.Count} emails");
            for (int i = 0; i < rule.Values.Count; i++)
            {
                if (rule.IsEnabled == false)
                {
                    continue;
                }

                string mr = rule.Values[i];
                for (int i1 = 0; i1 < emails.Count; i1++)
                {
                    Email email = emails[i1];
                    bool wasFound = false;
                    string? match = null;
                    try
                    {
                        if (rule.ExpressionType == ExpressionType.MatchesRegex)
                        {
                            switch (rule.LookIn)
                            {
                                case LookIn.All:
                                    Regex regex = new(mr, RegexOptions.IgnoreCase, new TimeSpan(0, 0, 5));
                                    if (CheckRegExMatches(email.Subject, regex, out match) ||
                                        CheckRegExMatches(email.TextBody, regex, out match) ||
                                        CheckRegExMatches(email.HtmlBody, regex, out match))
                                    {

                                        wasFound = true;
                                    }
                                    break;
                                case LookIn.Body:
                                    break;
                                case LookIn.Sender:
                                    break;
                                case LookIn.Recipient:
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }

                        }
                        else if (rule.ExpressionType == ExpressionType.Contains)
                        {
                            switch (rule.LookIn)
                            {
                                case LookIn.All:

                                    if (Contains(email.Subject, mr) ||
                                        Contains(email.SenderName, mr) ||
                                        Contains(email.TextBody, mr) ||
                                        Contains(email.HtmlBody, mr))
                                    {
                                        wasFound = true;
                                    }
                                    break;
                                case LookIn.Body:
                                    if (
                             Contains(email.TextBody, mr) ||
                             Contains(email.HtmlBody, mr))
                                    {
                                        wasFound = true;
                                    }
                                    break;
                                case LookIn.Subject:
                                    if (Contains(email.Subject, mr))
                                    {
                                        wasFound = true;
                                    }
                                    break;
                                case LookIn.Sender:
                                    if (
                             Contains(email.SenderName, mr))
                                    {
                                        wasFound = true;
                                    }
                                    break;
                                case LookIn.SenderEmail:
                                    if (
                             Contains(email.SenderEmailaddress, mr))
                                    {
                                        wasFound = true;
                                    }
                                    break;
                                case LookIn.Recipient:
                                    break;
                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                    }
                    catch (Exception e)
                    {

                        _logger.LogError(e, "RunRules Exception");

                    }
                    if (wasFound)
                    {
                        if (rule.DaysOld > 0)
                        {
                            if (DateTimeOffset.Now.Subtract(email.Date).TotalDays < rule.DaysOld)
                            {
                                continue;
                            }
                        }

                        if (mailSettings.TestMode)
                        {

                            string kw = mr;
                            if (match != null)
                            {
                                kw += $"({match.ToLower()})";
                            }
                            if (matchesFound.ContainsKey(kw))
                            {
                                matchesFound[kw] = matchesFound[kw] + 1;
                            }
                            else
                            {
                                matchesFound.Add(kw, 1);
                            }
                            string key = match ?? rule.Name;
                            _logger.LogInformation($"Move: {email.Id} - {email.SenderName} - {email.Subject} - {key}");
                        }
                        else
                        {
                            triggers.Add(new RuleTrigger()
                            {
                                Id = email.Id,
                                From = email.Folder,
                                To = rule.MoveTo
                            });
                        }
                        emails.RemoveAt(i1);
                    }
                }
            }

        }


        foreach (string key in matchesFound.Keys)
        {
            _logger.LogInformation($"{key} - {matchesFound[key]}");
        }
        return triggers;
    }
    public bool Contains(string w, string s)
    {
        return !string.IsNullOrEmpty(w) && w.Contains(s, StringComparison.CurrentCultureIgnoreCase);
    }
    public bool CheckRegExMatches(string s, Regex regex, out string? message)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            message = null;
            return false;
        }

        IEnumerable<string> matches = regex.Matches(s).Select(x => x.Value);
        if (!matches.Any())
        {
            message = null;
            return false;
        }

        message = matches.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(message))
        {
            Debugger.Break();
        }

        return true;
    }


    public void RunTriggers(List<RuleTrigger> triggers)

    {

        if (triggers.Count == 0)
        {
            return;
        }
        Dictionary<string, List<MoveTo>> sortedMails = new();
        foreach (RuleTrigger trigger in triggers)
        {
            List<MoveTo>? moveTos = null;
            if (!sortedMails.ContainsKey(trigger.From))
            {
                moveTos = new List<MoveTo>();

                sortedMails.Add(trigger.From, moveTos);
            }
            else
            {
                moveTos = sortedMails[trigger.From];

            }
            MoveTo moveTo = moveTos.FirstOrDefault(x => string.Equals(x.Folder, trigger.To, StringComparison.CurrentCultureIgnoreCase));

            if (moveTo == null)
            {
                moveTo = new MoveTo()
                {
                    Folder = trigger.To,
                    UniqueIds = new List<UniqueId>()
                };
                moveTos.Add(moveTo);
            }
            moveTo.UniqueIds.Add(trigger.Id);
        }

        using IDbConnection dbc = _mailDb.GetConnection();
        using ImapClient mc = _mailClientService.GetMailClient();

        foreach (string key in sortedMails.Keys)
        {
            IMailFolder mcFolder = mc.GetFolder(key);

            if (mcFolder != null)
            {
                _ = mcFolder.Open(FolderAccess.ReadWrite);
                foreach (MoveTo sm in sortedMails[key])
                {
                    IMailFolder moveToFolder = sm.Folder.ToLower() == "trash"
                        ? mc.Capabilities.HasFlag(ImapCapabilities.SpecialUse) ? mc.GetFolder(SpecialFolder.Trash) : mc.GetFolder(mailSettings.Trash)
                        : mc.GetFolder(sm.Folder);
                    if (moveToFolder != null)
                    {

                        _ = mcFolder.MoveTo(sm.UniqueIds, moveToFolder);


                        foreach (UniqueId uid in sm.UniqueIds)
                        {
                            try
                            {
                                _ = dbc.Update<Email>(new { sm.Folder }, x => x.Id == uid);
                            }
                            catch (Exception e)
                            {
                                _logger.LogError(e, "RunTriggers Exception");
                                Debugger.Break();
                            }
                        }

                    }
                }
            }
        }

        mc.Disconnect(true);
    }
}

public class MoveTo
{
    public string? Folder { get; set; }
    public List<UniqueId>? UniqueIds { get; set; }
}
