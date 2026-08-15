using Dapper;
using RoboDodd.OrmLite;

namespace MailZort.Data;

public record ActivityFilter(string? Search = null, string? EventType = null, bool UndoableOnly = false, int Limit = 200);

public interface IActivityStore
{
    Task<int> LogAsync(DbActivity entry, CancellationToken cancellationToken = default);
    Task LogManyAsync(IEnumerable<DbActivity> entries, CancellationToken cancellationToken = default);
    Task<List<DbActivity>> QueryAsync(ActivityFilter filter, CancellationToken cancellationToken = default);
    Task<DbActivity?> GetAsync(int id, CancellationToken cancellationToken = default);
    Task MarkUndoneAsync(int id, CancellationToken cancellationToken = default);
    Task<int> CountSinceAsync(DateTime sinceUtc, string? eventType = null, CancellationToken cancellationToken = default);
    Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);

    /// <summary>Raised after an entry is written, so the portal can push it to open pages.</summary>
    event Action<DbActivity>? Logged;
}

public class ActivityStore : IActivityStore
{
    private readonly ILogger<ActivityStore> _logger;
    private readonly IMailZortDatabase _db;

    public event Action<DbActivity>? Logged;

    public ActivityStore(ILogger<ActivityStore> logger, IMailZortDatabase db)
    {
        _logger = logger;
        _db = db;
    }

    public async Task<int> LogAsync(DbActivity entry, CancellationToken cancellationToken = default)
    {
        if (entry.CreatedUtc == default)
            entry.CreatedUtc = DateTime.UtcNow;

        try
        {
            using var connection = await _db.OpenAsync(cancellationToken);
            entry.Id = (int)await connection.InsertAsync(entry, selectIdentity: true);
        }
        catch (Exception ex)
        {
            // Never let bookkeeping break mail processing.
            _logger.LogError(ex, "Could not write activity entry {EventType}", entry.EventType);
            return 0;
        }

        NotifyLogged(entry);
        return entry.Id;
    }

    public async Task LogManyAsync(IEnumerable<DbActivity> entries, CancellationToken cancellationToken = default)
    {
        var list = entries.ToList();
        if (list.Count == 0)
            return;

        foreach (var entry in list)
        {
            if (entry.CreatedUtc == default)
                entry.CreatedUtc = DateTime.UtcNow;
        }

        try
        {
            using var connection = await _db.OpenAsync(cancellationToken);
            foreach (var entry in list)
            {
                entry.Id = (int)await connection.InsertAsync(entry, selectIdentity: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write {Count} activity entries", list.Count);
            return;
        }

        foreach (var entry in list)
        {
            NotifyLogged(entry);
        }
    }

    public async Task<List<DbActivity>> QueryAsync(ActivityFilter filter, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter.Search))
            where.Add("(Subject LIKE @search OR SenderAddress LIKE @search OR SenderName LIKE @search OR ToFolder LIKE @search)");
        if (!string.IsNullOrWhiteSpace(filter.EventType))
            where.Add("EventType = @eventType");
        if (filter.UndoableOnly)
            where.Add("CanUndo = 1 AND UndoneUtc IS NULL");

        var sql = "SELECT * FROM Activity"
                  + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
                  + " ORDER BY Id DESC LIMIT @limit";

        var rows = await connection.QueryAsync<DbActivity>(sql, new
        {
            search = $"%{filter.Search}%",
            eventType = filter.EventType,
            limit = filter.Limit
        });
        return rows.ToList();
    }

    public async Task<DbActivity?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        return await connection.QueryFirstOrDefaultAsync<DbActivity>(
            "SELECT * FROM Activity WHERE Id = @id", new { id });
    }

    public async Task MarkUndoneAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE Activity SET UndoneUtc = @now, CanUndo = 0 WHERE Id = @id",
            new { id, now = DateTime.UtcNow });
    }

    public async Task<int> CountSinceAsync(DateTime sinceUtc, string? eventType = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var sql = "SELECT COUNT(*) FROM Activity WHERE CreatedUtc >= @sinceUtc"
                  + (eventType != null ? " AND EventType = @eventType" : string.Empty);
        return await connection.ExecuteScalarAsync<int>(sql, new { sinceUtc, eventType });
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        return await connection.ExecuteAsync("DELETE FROM Activity WHERE CreatedUtc < @cutoffUtc", new { cutoffUtc });
    }

    private void NotifyLogged(DbActivity entry)
    {
        try
        {
            Logged?.Invoke(entry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An activity subscriber threw");
        }
    }
}
