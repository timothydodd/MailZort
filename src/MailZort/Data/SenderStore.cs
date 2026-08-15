using Dapper;
using RoboDodd.OrmLite;

namespace MailZort.Data;

public interface ISenderStore
{
    Task LoadAsync(CancellationToken cancellationToken);

    // Hot path - answered from the in-memory cache so rule processing never waits on the DB.
    SenderStatus GetStatus(string? senderAddress);
    bool IsTrusted(string? senderAddress);
    bool IsBlacklisted(string? senderAddress);

    Task RecordRescueAsync(string? senderAddress, string subject, CancellationToken cancellationToken = default);
    Task RecordSpamMoveAsync(string? senderAddress, string subject, CancellationToken cancellationToken = default);

    /// <summary>Portal override. setBy records who did it.</summary>
    Task SetStatusAsync(string senderAddress, SenderStatus status, string setBy, string? note = null,
        CancellationToken cancellationToken = default);
    Task RemoveAsync(string senderAddress, CancellationToken cancellationToken = default);

    Task<List<DbSender>> SearchAsync(string? search, SenderStatus? status, int limit = 500,
        CancellationToken cancellationToken = default);
    Task<(int trusted, int blacklisted)> CountsAsync(CancellationToken cancellationToken = default);
}

public class SenderStore : ISenderStore
{
    private readonly ILogger<SenderStore> _logger;
    private readonly IMailZortDatabase _db;
    private readonly IActivityStore _activity;
    private readonly ISettingsStore _settings;
    private readonly Dictionary<string, SenderStatus> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _cacheSync = new();

    public SenderStore(ILogger<SenderStore> logger, IMailZortDatabase db, IActivityStore activity,
        ISettingsStore settings)
    {
        _logger = logger;
        _db = db;
        _activity = activity;
        _settings = settings;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var senders = await connection.SelectAsync<DbSender>();

        lock (_cacheSync)
        {
            _cache.Clear();
            foreach (var sender in senders)
            {
                _cache[sender.Address] = (SenderStatus)sender.Status;
            }
        }

        _logger.LogInformation("Loaded {Total} senders ({Trusted} trusted, {Blacklisted} blacklisted)",
            senders.Count,
            senders.Count(s => s.Status == (int)SenderStatus.Trusted),
            senders.Count(s => s.Status == (int)SenderStatus.Blacklisted));
    }

    public SenderStatus GetStatus(string? senderAddress)
    {
        var key = Normalize(senderAddress);
        if (key == null)
            return SenderStatus.Neutral;

        lock (_cacheSync)
        {
            return _cache.TryGetValue(key, out var status) ? status : SenderStatus.Neutral;
        }
    }

    public bool IsTrusted(string? senderAddress) => GetStatus(senderAddress) == SenderStatus.Trusted;

    public bool IsBlacklisted(string? senderAddress) => GetStatus(senderAddress) == SenderStatus.Blacklisted;

    public async Task RecordRescueAsync(string? senderAddress, string subject, CancellationToken cancellationToken = default)
    {
        var key = Normalize(senderAddress);
        if (key == null)
            return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = await _db.OpenAsync(cancellationToken);
            var sender = await GetOrCreateAsync(connection, key, cancellationToken);
            var previous = (SenderStatus)sender.Status;

            sender.Rescues++;
            // A rescue is the strongest signal there is - it clears a blacklist outright.
            sender.SpamMoves = 0;
            sender.Status = (int)SenderStatus.Trusted;
            sender.LastSubject = subject;
            sender.SetBy = "observed";
            sender.UpdatedUtc = DateTime.UtcNow;
            await connection.UpdateAsync(sender);

            SetCache(key, SenderStatus.Trusted);

            await _activity.LogAsync(new DbActivity
            {
                EventType = ActivityType.Trusted,
                SenderAddress = key,
                Subject = subject,
                Detail = $"Rescued from Junk ({previous} -> Trusted, {sender.Rescues} rescue(s))"
            }, cancellationToken);

            _logger.LogInformation("Sender '{Sender}' rescued from spam - {Previous} -> Trusted", key, previous);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RecordSpamMoveAsync(string? senderAddress, string subject, CancellationToken cancellationToken = default)
    {
        var key = Normalize(senderAddress);
        if (key == null)
            return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = await _db.OpenAsync(cancellationToken);
            var sender = await GetOrCreateAsync(connection, key, cancellationToken);
            var previous = (SenderStatus)sender.Status;
            var threshold = Math.Max(1, _settings.GetInt(SettingKeys.BlacklistThreshold, 2));

            sender.SpamMoves++;
            // Trust is revoked on the first demotion; the blacklist lands at the threshold.
            var status = sender.SpamMoves >= threshold ? SenderStatus.Blacklisted : SenderStatus.Neutral;
            sender.Status = (int)status;
            sender.LastSubject = subject;
            sender.SetBy = "observed";
            sender.UpdatedUtc = DateTime.UtcNow;
            await connection.UpdateAsync(sender);

            SetCache(key, status);

            await _activity.LogAsync(new DbActivity
            {
                EventType = status == SenderStatus.Blacklisted ? ActivityType.Blacklisted : ActivityType.TrustRevoked,
                SenderAddress = key,
                Subject = subject,
                Detail = $"Moved to Junk ({sender.SpamMoves}/{threshold}), {previous} -> {status}"
            }, cancellationToken);

            _logger.LogInformation("Sender '{Sender}' moved to spam ({Count}/{Threshold}) - {Previous} -> {Status}",
                key, sender.SpamMoves, threshold, previous, status);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SetStatusAsync(string senderAddress, SenderStatus status, string setBy, string? note = null,
        CancellationToken cancellationToken = default)
    {
        var key = Normalize(senderAddress);
        if (key == null)
            throw new ArgumentException("Not an email address", nameof(senderAddress));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            using var connection = await _db.OpenAsync(cancellationToken);
            var sender = await GetOrCreateAsync(connection, key, cancellationToken);
            var previous = (SenderStatus)sender.Status;

            sender.Status = (int)status;
            sender.SetBy = setBy;
            sender.Note = note ?? sender.Note;
            // A manual verdict clears the counter that argued the other way.
            if (status == SenderStatus.Trusted) sender.SpamMoves = 0;
            if (status == SenderStatus.Blacklisted) sender.Rescues = 0;
            sender.UpdatedUtc = DateTime.UtcNow;
            await connection.UpdateAsync(sender);

            SetCache(key, status);

            await _activity.LogAsync(new DbActivity
            {
                EventType = status switch
                {
                    SenderStatus.Trusted => ActivityType.Trusted,
                    SenderStatus.Blacklisted => ActivityType.Blacklisted,
                    _ => ActivityType.TrustRevoked
                },
                SenderAddress = key,
                Detail = $"Set to {status} by {setBy} (was {previous})"
            }, cancellationToken);

            _logger.LogInformation("Sender '{Sender}' set to {Status} by {SetBy}", key, status, setBy);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task RemoveAsync(string senderAddress, CancellationToken cancellationToken = default)
    {
        var key = Normalize(senderAddress);
        if (key == null)
            return;

        using var connection = await _db.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("DELETE FROM Senders WHERE Address = @key", new { key });

        lock (_cacheSync)
        {
            _cache.Remove(key);
        }
    }

    public async Task<List<DbSender>> SearchAsync(string? search, SenderStatus? status, int limit = 500,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(search)) where.Add("Address LIKE @search");
        if (status.HasValue) where.Add("Status = @status");

        var sql = "SELECT * FROM Senders"
                  + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
                  + " ORDER BY UpdatedUtc DESC LIMIT @limit";

        var rows = await connection.QueryAsync<DbSender>(sql,
            new { search = $"%{search}%", status = (int?)status, limit });
        return rows.ToList();
    }

    public async Task<(int trusted, int blacklisted)> CountsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var trusted = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Senders WHERE Status = @status", new { status = (int)SenderStatus.Trusted });
        var blacklisted = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Senders WHERE Status = @status", new { status = (int)SenderStatus.Blacklisted });
        return (trusted, blacklisted);
    }

    private static async Task<DbSender> GetOrCreateAsync(System.Data.IDbConnection connection, string key,
        CancellationToken cancellationToken)
    {
        var existing = await connection.QueryFirstOrDefaultAsync<DbSender>(
            "SELECT * FROM Senders WHERE Address = @key", new { key });
        if (existing != null)
            return existing;

        var created = new DbSender
        {
            Address = key,
            Status = (int)SenderStatus.Neutral,
            FirstSeenUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        await connection.InsertAsync(created);
        return created;
    }

    private void SetCache(string key, SenderStatus status)
    {
        lock (_cacheSync)
        {
            _cache[key] = status;
        }
    }

    /// <summary>
    /// Reputation is keyed on the first From address, lowercased. SenderAddress can hold several
    /// addresses joined with ';' when a message has multiple From mailboxes.
    /// </summary>
    public static string? Normalize(string? senderAddress)
    {
        if (string.IsNullOrWhiteSpace(senderAddress))
            return null;

        var first = senderAddress
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(first) || !first.Contains('@'))
            return null;

        return first.ToLowerInvariant();
    }
}
