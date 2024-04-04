using System.Data;
using System.Diagnostics;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using ServiceStack.OrmLite;

namespace MailZort.Services;

internal class MailClient
{


    private readonly MailDb _mailDb;
    private readonly ILogger<MailClient> _logger;

    public MailClient(EmailSettings ops, MailDb mailDb, ILogger<MailClient> logger)
    {
        _ops = ops;
        _mailDb = mailDb;
        _logger = logger;
    }

    private EmailSettings _ops { get; }

    public ImapClient GetMailClient()
    {
        ImapClient client = new();
        _logger.LogInformation($"Connecting to {_ops.Server}:{_ops.Port}");
        client.Connect(_ops.Server, _ops.Port, _ops.UseSsl);
        client.Authenticate(_ops.Username, _ops.Password);
        return client;
    }
    public void GetMessages(IEnumerable<Rule>? rules)
    {
        if (rules == null || !rules.Any())
        {
            return;
        }
        using IDbConnection dbc = _mailDb.GetConnection();
        IEnumerable<UniqueId> s1 = dbc.Select<UniqueId>("SELECT Id FROM Email");
        HashSet<UniqueId> emailIds = s1.ToHashSet();

        foreach (string? folderName in rules.Select(x => x.Folder).Distinct())
        {
            using ImapClient client = GetMailClient();

            IMailFolder inbox = client.GetFolder(folderName);
            client.Inbox.Open(FolderAccess.ReadOnly);

            SearchQuery searchMode = SearchQuery.Recent;

            searchMode = (_ops.SearchMode?.ToLower()) switch
            {
                "all" => SearchQuery.All,
                "not-seen" => SearchQuery.NotSeen,
                _ => SearchQuery.Recent,
            };

            if (searchMode == SearchQuery.All)
            {
                dbc.DeleteAll<Email>();
            }
            IList<UniqueId> uniqueIds = client.Inbox.Search(searchMode);
            client.Disconnect(true);

            var threadCount = 1;
            List<List<UniqueId>> splitList = new();
            if (uniqueIds.Count > 50)
            {
                threadCount = uniqueIds.Count / 50;
                splitList = SplitUids(uniqueIds, threadCount);
            }
            else
            {
                splitList = new List<List<UniqueId>>() { uniqueIds.ToList() };
            }



            List<Email> results = new();
            Task[] tasks = new Task[threadCount];
            _logger.LogInformation($"Fetching Emails:{searchMode}");
            Stopwatch sw = new();
            sw.Start();

            for (int index = 0; index < tasks.Length; index++)
            {
                int uniqueNum = index;
                tasks[index] = Task.Run(async () =>
                {
                    await GetMessage(folderName, splitList[uniqueNum], emailIds, results);
                });
            }

            Task.WaitAll(tasks);
            dbc.InsertAll(results);
            sw.Stop();
            _logger.LogInformation($"Found {results.Count} emails");
            _logger.LogInformation($"Done in {sw.Elapsed.TotalSeconds}secs");
        }

    }
    private async Task GetMessage(string folderName, IList<UniqueId> ids, HashSet<UniqueId> emailIds, List<Email> results)
    {
        using ImapClient client = GetMailClient();
        IMailFolder inbox = client.GetFolder(folderName);
        client.Inbox.Open(FolderAccess.ReadOnly);
        foreach (UniqueId uniqueId in ids)
        {
            if (emailIds.Contains(uniqueId))
            {
                continue;
            }

            MimeMessage message = await inbox.GetMessageAsync(uniqueId);


            string senderNames = string.Join(";", message.From.Select(x => x.Name));
            string senderAddress = string.Join(";", message.From.Select(x => (x as MailboxAddress)?.Address));



            results.Add(new Email(
                senderNames,
                senderAddress,
                uniqueId.Id,
                message.Subject,
                message.TextBody,
                message.HtmlBody, inbox.Name,
                message.Date));
        }
        client.Disconnect(true);
    }
    private List<List<UniqueId>> SplitUids(IList<UniqueId> uIds, int threadCount)
    {
        List<List<UniqueId>> list = new();

        for (int list_index = 0; list_index < threadCount; list_index++)
        {
            List<UniqueId> sublist = new();
            list.Add(sublist);
        }
        // should have {_num_of_threads} lists now

        //now spread the uids across the lists
        int i = 0;
        for (int uid_index = 0; uid_index < uIds.Count; uid_index++)
        {
            list[i].Add(uIds[uid_index]);
            i++;

            if (i == threadCount)
            {
                i = 0;
            }
        }
        return list;
    }
}
