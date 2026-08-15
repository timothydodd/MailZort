using System.Security.Cryptography;
using Dapper;
using RoboDodd.OrmLite;

namespace MailZort.Data;

public interface IUserStore
{
    /// <summary>Creates the default admin/admin account when there are no users at all.</summary>
    Task SeedDefaultUserAsync(CancellationToken cancellationToken = default);
    Task<DbUser?> ValidateAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<DbUser?> FindAsync(string username, CancellationToken cancellationToken = default);
    Task ChangePasswordAsync(string username, string newPassword, CancellationToken cancellationToken = default);
    Task ChangeUsernameAsync(string currentUsername, string newUsername, CancellationToken cancellationToken = default);
    Task RecordLoginAsync(string username, CancellationToken cancellationToken = default);
}

public class UserStore : IUserStore
{
    public const string DefaultUsername = "admin";
    public const string DefaultPassword = "admin";

    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 210_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    private readonly ILogger<UserStore> _logger;
    private readonly IMailZortDatabase _db;

    public UserStore(ILogger<UserStore> logger, IMailZortDatabase db)
    {
        _logger = logger;
        _db = db;
    }

    public async Task SeedDefaultUserAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users");
        if (count > 0)
            return;

        await connection.InsertAsync(new DbUser
        {
            Username = DefaultUsername,
            PasswordHash = HashPassword(DefaultPassword),
            MustChangePassword = true,
            CreatedUtc = DateTime.UtcNow
        });

        _logger.LogWarning("Created the default portal login '{Username}' with password '{Password}' - change it on first sign-in",
            DefaultUsername, DefaultPassword);
    }

    public async Task<DbUser?> ValidateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var user = await FindAsync(username, cancellationToken);
        if (user == null)
            return null;

        return VerifyPassword(password, user.PasswordHash) ? user : null;
    }

    public async Task<DbUser?> FindAsync(string username, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        return await connection.QueryFirstOrDefaultAsync<DbUser>(
            "SELECT * FROM Users WHERE Username = @username", new { username });
    }

    public async Task ChangePasswordAsync(string username, string newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            throw new ArgumentException("Password must be at least 6 characters", nameof(newPassword));

        using var connection = await _db.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            "UPDATE Users SET PasswordHash = @hash, MustChangePassword = 0 WHERE Username = @username",
            new { username, hash = HashPassword(newPassword) });

        _logger.LogInformation("Password changed for portal user '{Username}'", username);
    }

    public async Task ChangeUsernameAsync(string currentUsername, string newUsername, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newUsername))
            throw new ArgumentException("Username is required", nameof(newUsername));

        using var connection = await _db.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("UPDATE Users SET Username = @newUsername WHERE Username = @currentUsername",
            new { currentUsername, newUsername = newUsername.Trim() });
    }

    public async Task RecordLoginAsync(string username, CancellationToken cancellationToken = default)
    {
        using var connection = await _db.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("UPDATE Users SET LastLoginUtc = @now WHERE Username = @username",
            new { username, now = DateTime.UtcNow });
    }

    /// <summary>PBKDF2-SHA256. Stored as iterations.salt.key so the cost can be raised later.</summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
