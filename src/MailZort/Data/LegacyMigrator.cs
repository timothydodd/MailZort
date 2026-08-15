using System.Text.Json;
using Dapper;
using RoboDodd.OrmLite;

namespace MailZort.Data;

/// <summary>
/// One-time import of the file-based configuration this app used before the portal:
/// rules from appsettings.json and reputation from sender-reputation.json.
/// Both are no-ops once the database holds the data.
/// </summary>
public class LegacyMigrator
{
    private readonly ILogger<LegacyMigrator> _logger;
    private readonly IMailZortDatabase _db;
    private readonly IConfiguration _configuration;
    private readonly EmailSettings _config;

    public LegacyMigrator(ILogger<LegacyMigrator> logger, IMailZortDatabase db, IConfiguration configuration,
        EmailSettings config)
    {
        _logger = logger;
        _db = db;
        _configuration = configuration;
        _config = config;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await MigrateRulesAsync(cancellationToken);
        await MigrateReputationAsync(cancellationToken);
    }

    private async Task MigrateRulesAsync(CancellationToken cancellationToken)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var existing = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Rules");
        if (existing > 0)
            return;

        var rules = _configuration.GetSection("Rules").Get<List<Rule>>();
        if (rules == null || rules.Count == 0)
        {
            _logger.LogInformation("No rules in appsettings.json to migrate - starting with an empty rule set");
            return;
        }

        var order = 1;
        foreach (var rule in rules)
        {
            var row = RuleStore.FromDomain(rule, order++);
            row.Id = (int)await connection.InsertAsync(row, selectIdentity: true);

            foreach (var value in rule.Values ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    await connection.InsertAsync(new DbRuleValue { RuleId = row.Id, Value = value });
                }
            }
        }

        _logger.LogInformation("Migrated {Count} rules from appsettings.json into the database. " +
                               "Rules are now edited in the portal - the appsettings copy is ignored from here on",
            rules.Count);
    }

    private async Task MigrateReputationAsync(CancellationToken cancellationToken)
    {
        var dir = Path.IsPathRooted(_config.DataDirectory)
            ? _config.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, _config.DataDirectory);
        var path = Path.Combine(dir, "sender-reputation.json");

        if (!File.Exists(path))
            return;

        try
        {
            using var connection = await _db.OpenAsync(cancellationToken);
            var existing = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Senders");
            if (existing > 0)
                return;

            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<LegacyReputationState>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);

            if (state?.Senders != null)
            {
                foreach (var (address, sender) in state.Senders)
                {
                    await connection.InsertAsync(new DbSender
                    {
                        Address = address.ToLowerInvariant(),
                        Status = (int)sender.Status,
                        Rescues = sender.Rescues,
                        SpamMoves = sender.SpamMoves,
                        LastSubject = sender.LastSubject,
                        SetBy = "migrated",
                        FirstSeenUtc = sender.FirstSeenUtc == default ? DateTime.UtcNow : sender.FirstSeenUtc.UtcDateTime,
                        UpdatedUtc = DateTime.UtcNow
                    });
                }

                _logger.LogInformation("Migrated {Count} senders from sender-reputation.json", state.Senders.Count);
            }

            if (state?.Snapshot != null)
            {
                foreach (var (key, folder) in state.Snapshot)
                {
                    await connection.InsertAsync(new DbMessageLocation
                    {
                        MessageKey = key,
                        Folder = folder,
                        Suppress = state.SuppressedKeys?.Contains(key) == true,
                        SeenUtc = DateTime.UtcNow
                    });
                }
            }

            // Keep the file, but out of the way, so the import cannot run twice.
            File.Move(path, path + ".migrated", overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not migrate sender-reputation.json - continuing with an empty reputation table");
        }
    }

    private class LegacyReputationState
    {
        public Dictionary<string, LegacySender>? Senders { get; set; }
        public Dictionary<string, string>? Snapshot { get; set; }
        public HashSet<string>? SuppressedKeys { get; set; }
    }

    private class LegacySender
    {
        public SenderStatus Status { get; set; }
        public int Rescues { get; set; }
        public int SpamMoves { get; set; }
        public string? LastSubject { get; set; }
        public DateTimeOffset FirstSeenUtc { get; set; }
    }
}
