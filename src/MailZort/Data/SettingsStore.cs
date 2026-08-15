using Dapper;
using RoboDodd.OrmLite;

namespace MailZort.Data;

public static class SettingKeys
{
    public const string SenderReputationEnabled = "SenderReputationEnabled";
    public const string BlacklistThreshold = "BlacklistThreshold";
    public const string ReputationLookbackDays = "ReputationLookbackDays";
    public const string ReputationWatchFolders = "ReputationWatchFolders";
    public const string PurgeFutureDatedEnabled = "PurgeFutureDatedEnabled";
    public const string FutureDatedToleranceMinutes = "FutureDatedToleranceMinutes";
    public const string InboxCleanupEnabled = "InboxCleanupEnabled";
    public const string InboxCleanupDaysOld = "InboxCleanupDaysOld";
    public const string ImportantFolder = "ImportantFolder";
    public const string JunkFolder = "JunkFolder";
    public const string TrashFolder = "TrashFolder";
    public const string ActivityRetentionDays = "ActivityRetentionDays";
}

public interface ISettingsStore
{
    Task LoadAsync(CancellationToken cancellationToken);
    /// <summary>Writes any key that has no row yet, using the values from appsettings.json.</summary>
    Task SeedDefaultsAsync(EmailSettings config, CancellationToken cancellationToken);

    string? Get(string key);
    Task<int> GetIntAsync(string key, CancellationToken cancellationToken = default);
    int GetInt(string key, int fallback = 0);
    bool GetBool(string key, bool fallback = false);
    List<string> GetList(string key);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> AllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Behaviour settings, editable in the portal. Cached in memory because the mail loop reads
/// them per message; writes go through this class so the cache never drifts.
/// </summary>
public class SettingsStore : ISettingsStore
{
    private readonly ILogger<SettingsStore> _logger;
    private readonly IMailZortDatabase _db;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public SettingsStore(ILogger<SettingsStore> logger, IMailZortDatabase db)
    {
        _logger = logger;
        _db = db;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var rows = await connection.SelectAsync<DbSetting>();

        lock (_sync)
        {
            _cache.Clear();
            foreach (var row in rows)
            {
                _cache[row.Key] = row.Value;
            }
        }

        _logger.LogDebug("Loaded {Count} settings", rows.Count);
    }

    public async Task SeedDefaultsAsync(EmailSettings config, CancellationToken cancellationToken)
    {
        var defaults = new Dictionary<string, string>
        {
            [SettingKeys.SenderReputationEnabled] = config.SenderReputationEnabled.ToString(),
            [SettingKeys.BlacklistThreshold] = config.BlacklistThreshold.ToString(),
            [SettingKeys.ReputationLookbackDays] = config.ReputationLookbackDays.ToString(),
            [SettingKeys.ReputationWatchFolders] = string.Join(",", config.ReputationWatchFolders),
            [SettingKeys.PurgeFutureDatedEnabled] = config.PurgeFutureDatedEnabled.ToString(),
            [SettingKeys.FutureDatedToleranceMinutes] = config.FutureDatedToleranceMinutes.ToString(),
            [SettingKeys.InboxCleanupEnabled] = config.InboxCleanupEnabled.ToString(),
            [SettingKeys.InboxCleanupDaysOld] = config.InboxCleanupDaysOld.ToString(),
            [SettingKeys.ImportantFolder] = config.ImportantFolder,
            [SettingKeys.JunkFolder] = config.Junk,
            [SettingKeys.TrashFolder] = config.Trash ?? "Trash",
            [SettingKeys.ActivityRetentionDays] = "90"
        };

        using var connection = await _db.OpenAsync(cancellationToken);
        var seeded = 0;

        foreach (var (key, value) in defaults)
        {
            var exists = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Settings WHERE Key = @key", new { key });
            if (exists > 0)
                continue;

            await connection.InsertAsync(new DbSetting { Key = key, Value = value, UpdatedUtc = DateTime.UtcNow });
            seeded++;

            lock (_sync)
            {
                _cache[key] = value;
            }
        }

        if (seeded > 0)
        {
            _logger.LogInformation("Seeded {Count} settings from appsettings.json", seeded);
        }
    }

    public string? Get(string key)
    {
        lock (_sync)
        {
            return _cache.TryGetValue(key, out var value) ? value : null;
        }
    }

    public Task<int> GetIntAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetInt(key));

    public int GetInt(string key, int fallback = 0) =>
        int.TryParse(Get(key), out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        bool.TryParse(Get(key), out var value) ? value : fallback;

    public List<string> GetList(string key) =>
        (Get(key) ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var exists = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Settings WHERE Key = @key", new { key });

        if (exists > 0)
        {
            await connection.ExecuteAsync(
                "UPDATE Settings SET Value = @value, UpdatedUtc = @now WHERE Key = @key",
                new { key, value, now = DateTime.UtcNow });
        }
        else
        {
            await connection.InsertAsync(new DbSetting { Key = key, Value = value, UpdatedUtc = DateTime.UtcNow });
        }

        lock (_sync)
        {
            _cache[key] = value;
        }
    }

    public async Task<Dictionary<string, string>> AllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var rows = await connection.SelectAsync<DbSetting>();
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
    }
}
