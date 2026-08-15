using Dapper;
using RoboDodd.OrmLite;

namespace MailZort.Data;

public interface IRuleStore
{
    Task LoadAsync(CancellationToken cancellationToken);

    /// <summary>Enabled rules in sort order, served from cache for the mail loop.</summary>
    IReadOnlyList<Rule> ActiveRules { get; }

    Task<List<DbRule>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<(DbRule rule, List<string> values)?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<int> SaveAsync(DbRule rule, List<string> values, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default);
    Task MoveAsync(int id, int direction, CancellationToken cancellationToken = default);
    Task RecordMatchesAsync(IEnumerable<string> ruleNames, CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    event Action? RulesChanged;
}

/// <summary>
/// Rules live in the database so the portal can edit them. Changes take effect on the next
/// batch without a restart - the mail loop reads <see cref="ActiveRules"/> each pass.
/// </summary>
public class RuleStore : IRuleStore
{
    private readonly ILogger<RuleStore> _logger;
    private readonly IMailZortDatabase _db;
    private volatile List<Rule> _active = new();

    public event Action? RulesChanged;

    public RuleStore(ILogger<RuleStore> logger, IMailZortDatabase db)
    {
        _logger = logger;
        _db = db;
    }

    public IReadOnlyList<Rule> ActiveRules => _active;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var connection = await _db.OpenAsync(cancellationToken);

        var rules = (await connection.QueryAsync<DbRule>(
            "SELECT * FROM Rules WHERE IsEnabled = 1 ORDER BY SortOrder, Id")).ToList();
        var values = (await connection.QueryAsync<DbRuleValue>("SELECT * FROM RuleValues")).ToList();
        var byRule = values.GroupBy(v => v.RuleId).ToDictionary(g => g.Key, g => g.Select(v => v.Value).ToList());

        _active = rules.Select(r => ToDomain(r, byRule.TryGetValue(r.Id, out var v) ? v : new List<string>())).ToList();

        _logger.LogInformation("Loaded {Count} enabled rules", _active.Count);
        RulesChanged?.Invoke();
    }

    public async Task<List<DbRule>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DbRule>("SELECT * FROM Rules ORDER BY SortOrder, Id");
        return rows.ToList();
    }

    public async Task<(DbRule rule, List<string> values)?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var rule = await connection.QueryFirstOrDefaultAsync<DbRule>("SELECT * FROM Rules WHERE Id = @id", new { id });
        if (rule == null)
            return null;

        var values = (await connection.QueryAsync<string>(
            "SELECT Value FROM RuleValues WHERE RuleId = @id ORDER BY Id", new { id })).ToList();
        return (rule, values);
    }

    public async Task<int> SaveAsync(DbRule rule, List<string> values, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        rule.UpdatedUtc = DateTime.UtcNow;

        if (rule.Id == 0)
        {
            rule.CreatedUtc = DateTime.UtcNow;
            if (rule.SortOrder == 0)
            {
                rule.SortOrder = await connection.ExecuteScalarAsync<int>(
                    "SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM Rules");
            }
            rule.Id = (int)await connection.InsertAsync(rule, selectIdentity: true);
        }
        else
        {
            await connection.UpdateAsync(rule);
            await connection.ExecuteAsync("DELETE FROM RuleValues WHERE RuleId = @id", new { id = rule.Id });
        }

        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            await connection.InsertAsync(new DbRuleValue { RuleId = rule.Id, Value = value.Trim() });
        }

        await LoadAsync(cancellationToken);
        return rule.Id;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM RuleValues WHERE RuleId = @id", new { id });
        await connection.ExecuteAsync("DELETE FROM Rules WHERE Id = @id", new { id });
        await LoadAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("UPDATE Rules SET IsEnabled = @enabled, UpdatedUtc = @now WHERE Id = @id",
            new { id, enabled, now = DateTime.UtcNow });
        await LoadAsync(cancellationToken);
    }

    /// <summary>Rule order decides which one wins, so the portal can nudge a rule up or down.</summary>
    public async Task MoveAsync(int id, int direction, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var ordered = (await connection.QueryAsync<DbRule>("SELECT * FROM Rules ORDER BY SortOrder, Id")).ToList();
        var index = ordered.FindIndex(r => r.Id == id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= ordered.Count)
            return;

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);

        for (var i = 0; i < ordered.Count; i++)
        {
            await connection.ExecuteAsync("UPDATE Rules SET SortOrder = @order WHERE Id = @id",
                new { id = ordered[i].Id, order = i + 1 });
        }

        await LoadAsync(cancellationToken);
    }

    public async Task RecordMatchesAsync(IEnumerable<string> ruleNames, CancellationToken cancellationToken = default)
    {
        var counts = ruleNames.Where(n => !string.IsNullOrWhiteSpace(n))
            .GroupBy(n => n)
            .ToDictionary(g => g.Key, g => g.Count());
        if (counts.Count == 0)
            return;

        try
        {
            using var connection = await _db.OpenAsync(cancellationToken);
            foreach (var (name, count) in counts)
            {
                await connection.ExecuteAsync(
                    "UPDATE Rules SET MatchCount = MatchCount + @count, LastMatchedUtc = @now WHERE Name = @name",
                    new { name, count, now = DateTime.UtcNow });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update rule match counts");
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Rules");
    }

    public static Rule ToDomain(DbRule rule, List<string> values) => new()
    {
        Name = rule.Name,
        Folder = rule.Folder,
        MoveTo = rule.MoveTo,
        Action = (RuleAction)rule.Action,
        LookIn = (LookIn)rule.LookIn,
        ExpressionType = (ExpressionType)rule.ExpressionType,
        IsOr = rule.IsOr,
        DaysOld = rule.DaysOld,
        RequireUnread = rule.RequireUnread,
        RequireNotImportant = rule.RequireNotImportant,
        IsEnabled = rule.IsEnabled,
        Values = values
    };

    public static DbRule FromDomain(Rule rule, int sortOrder) => new()
    {
        Name = rule.Name ?? "Unnamed",
        Folder = rule.Folder ?? "Inbox",
        MoveTo = rule.MoveTo,
        Action = (int)rule.Action,
        LookIn = (int)rule.LookIn,
        ExpressionType = (int)rule.ExpressionType,
        IsOr = rule.IsOr,
        DaysOld = rule.DaysOld,
        RequireUnread = rule.RequireUnread,
        RequireNotImportant = rule.RequireNotImportant,
        IsEnabled = rule.IsEnabled,
        SortOrder = sortOrder,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow
    };
}
