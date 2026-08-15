using System.Data;
using System.Data.Common;
using Dapper;
using RoboDodd.OrmLite;

namespace MailZort.Data;

public interface IMailZortDatabase
{
    /// <summary>Opens a connection. Callers own it and must dispose it.</summary>
    Task<IDbConnection> OpenAsync(CancellationToken cancellationToken = default);
    Task InitializeAsync(CancellationToken cancellationToken = default);
    bool IsSqlite { get; }
}

/// <summary>
/// Owns the schema. Tables are created from the models in Models.cs and evolved in place -
/// CreateTableIfNotExistsAsync(migrateSchema: true) adds any column a model gained since last run.
/// </summary>
public class MailZortDatabase : IMailZortDatabase
{
    private readonly ILogger<MailZortDatabase> _logger;
    private readonly DbConnectionFactory _factory;
    private readonly string _connectionString;

    public bool IsSqlite { get; }

    public MailZortDatabase(ILogger<MailZortDatabase> logger, EmailSettings config)
    {
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            _connectionString = config.ConnectionString;
            IsSqlite = _connectionString.Contains("Data Source", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var dir = Path.IsPathRooted(config.DataDirectory)
                ? config.DataDirectory
                : Path.Combine(AppContext.BaseDirectory, config.DataDirectory);
            Directory.CreateDirectory(dir);
            _connectionString = $"Data Source={Path.Combine(dir, "mailzort.db")}";
            IsSqlite = true;
        }

        _factory = new DbConnectionFactory(_connectionString);
    }

    public async Task<IDbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = _factory.CreateDbConnection();
        if (connection is DbConnection async)
        {
            await async.OpenAsync(cancellationToken);
        }
        else
        {
            connection.Open();
        }

        if (IsSqlite)
        {
            // WAL lets the portal read while the mail loop writes; the timeout absorbs the rest.
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        }

        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await OpenAsync(cancellationToken);

        await connection.CreateTableIfNotExistsAsync<DbRule>(migrateSchema: true);
        await connection.CreateTableIfNotExistsAsync<DbRuleValue>(migrateSchema: true);
        await connection.CreateTableIfNotExistsAsync<DbSender>(migrateSchema: true);
        await connection.CreateTableIfNotExistsAsync<DbMessageLocation>(migrateSchema: true);
        await connection.CreateTableIfNotExistsAsync<DbActivity>(migrateSchema: true);
        await connection.CreateTableIfNotExistsAsync<DbPendingAction>(migrateSchema: true);
        await connection.CreateTableIfNotExistsAsync<DbSetting>(migrateSchema: true);
        await connection.CreateTableIfNotExistsAsync<DbUser>(migrateSchema: true);

        _logger.LogInformation("Database ready ({Provider})", IsSqlite ? "SQLite" : "MySQL");
    }
}
