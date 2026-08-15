using Dapper;

namespace MailZort.Data;

public interface IMessageLocationStore
{
    /// <summary>Message key -> folder, as of the previous observation pass.</summary>
    Task<Dictionary<string, string>> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<HashSet<string>> GetSuppressedAsync(CancellationToken cancellationToken = default);
    Task ReplaceSnapshotAsync(Dictionary<string, string> snapshot, CancellationToken cancellationToken = default);
    /// <summary>Marks a message as moved by MailZort, so the next pass ignores that transition.</summary>
    Task SuppressAsync(IEnumerable<string> messageKeys, CancellationToken cancellationToken = default);
}

public class MessageLocationStore : IMessageLocationStore
{
    private readonly IMailZortDatabase _db;

    public MessageLocationStore(IMailZortDatabase db) => _db = db;

    public async Task<Dictionary<string, string>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string MessageKey, string Folder)>(
            "SELECT MessageKey, Folder FROM MessageLocations");
        return rows.ToDictionary(r => r.MessageKey, r => r.Folder, StringComparer.Ordinal);
    }

    public async Task<HashSet<string>> GetSuppressedAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(
            "SELECT MessageKey FROM MessageLocations WHERE Suppress = 1");
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    public async Task ReplaceSnapshotAsync(Dictionary<string, string> snapshot,
        CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        // Suppressions are single-use: this pass consumed them.
        await connection.ExecuteAsync("DELETE FROM MessageLocations", transaction: transaction);

        var now = DateTime.UtcNow;
        foreach (var (key, folder) in snapshot)
        {
            await connection.ExecuteAsync(
                "INSERT INTO MessageLocations (MessageKey, Folder, Suppress, SeenUtc) VALUES (@key, @folder, 0, @now)",
                new { key, folder, now }, transaction);
        }

        transaction.Commit();
    }

    public async Task SuppressAsync(IEnumerable<string> messageKeys, CancellationToken cancellationToken = default)
    {
        var keys = messageKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
        if (keys.Count == 0)
            return;

        using var connection = await _db.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        var now = DateTime.UtcNow;
        foreach (var key in keys)
        {
            // The row may not exist yet when we move a message the same pass we first saw it.
            var updated = await connection.ExecuteAsync(
                "UPDATE MessageLocations SET Suppress = 1 WHERE MessageKey = @key", new { key }, transaction);
            if (updated == 0)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO MessageLocations (MessageKey, Folder, Suppress, SeenUtc) VALUES (@key, '', 1, @now)",
                    new { key, now }, transaction);
            }
        }

        transaction.Commit();
    }
}
