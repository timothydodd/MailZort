using Dapper;
using RoboDodd.OrmLite;

namespace MailZort.Data;

public interface IPendingActionStore
{
    Task<int> EnqueueAsync(DbPendingAction action, CancellationToken cancellationToken = default);
    Task<List<DbPendingAction>> TakePendingAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task CompleteAsync(int id, CancellationToken cancellationToken = default);
    Task FailAsync(int id, string error, CancellationToken cancellationToken = default);
    Task<int> PendingCountAsync(CancellationToken cancellationToken = default);
    Task<List<DbPendingAction>> FailedAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task RetryAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Raised when the portal queues work, so the mail loop can wake up instead of waiting for IDLE to end.</summary>
    event Action? ActionQueued;
}

public class PendingActionStore : IPendingActionStore
{
    private const int MaxAttempts = 3;

    private readonly IMailZortDatabase _db;

    public event Action? ActionQueued;

    public PendingActionStore(IMailZortDatabase db) => _db = db;

    public async Task<int> EnqueueAsync(DbPendingAction action, CancellationToken cancellationToken = default)
    {
        action.CreatedUtc = DateTime.UtcNow;
        action.Status = PendingActionStatus.Pending;

        using var connection = await _db.OpenAsync(cancellationToken);
        action.Id = (int)await connection.InsertAsync(action, selectIdentity: true);

        ActionQueued?.Invoke();
        return action.Id;
    }

    public async Task<List<DbPendingAction>> TakePendingAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DbPendingAction>(
            "SELECT * FROM PendingActions WHERE Status = @status AND Attempts < @maxAttempts ORDER BY Id LIMIT @limit",
            new { status = PendingActionStatus.Pending, maxAttempts = MaxAttempts, limit });
        return rows.ToList();
    }

    public async Task CompleteAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE PendingActions SET Status = @status, CompletedUtc = @now, Attempts = Attempts + 1 WHERE Id = @id",
            new { id, status = PendingActionStatus.Done, now = DateTime.UtcNow });
    }

    public async Task FailAsync(int id, string error, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        // Stays Pending for a retry until it runs out of attempts, then parks as Failed.
        await connection.ExecuteAsync(
            @"UPDATE PendingActions
                 SET Attempts = Attempts + 1,
                     Error = @error,
                     Status = CASE WHEN Attempts + 1 >= @maxAttempts THEN @failed ELSE @pending END
               WHERE Id = @id",
            new { id, error, maxAttempts = MaxAttempts, failed = PendingActionStatus.Failed, pending = PendingActionStatus.Pending });
    }

    public async Task<int> PendingCountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM PendingActions WHERE Status = @status", new { status = PendingActionStatus.Pending });
    }

    public async Task<List<DbPendingAction>> FailedAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DbPendingAction>(
            "SELECT * FROM PendingActions WHERE Status = @status ORDER BY Id DESC LIMIT @limit",
            new { status = PendingActionStatus.Failed, limit });
        return rows.ToList();
    }

    /// <summary>Puts a parked action back in the queue with its attempt count cleared.</summary>
    public async Task RetryAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE PendingActions SET Status = @pending, Attempts = 0, Error = NULL WHERE Id = @id",
            new { id, pending = PendingActionStatus.Pending });

        ActionQueued?.Invoke();
    }
}
