using System.Data;
using ServiceStack.DataAnnotations;
using ServiceStack.OrmLite;

namespace MailZort;

public class MailDb
{
    private readonly SettingsHelper _settingsHelper;

    public MailDb(SettingsHelper settingsHelper)
    {
        string path = Path.Combine(settingsHelper.SettingPath, @"mailv2.db");

        OrmLiteConnectionFactory db = new(path, SqliteDialect.Provider);

        using IDbConnection dbc = db.Open();

        _ = dbc.CreateTableIfNotExists<Email>();

        _settingsHelper = settingsHelper;
    }

    public IDbConnection GetConnection()
    {
        string path = Path.Combine(_settingsHelper.SettingPath, @"mailv2.db");

        OrmLiteConnectionFactory db = new(path, SqliteDialect.Provider);

        return db.Open();
    }
}

public class Email
{
    [AutoIncrement] // Automatically increment the Id field
    [PrimaryKey]
    public int Id { get; set; } // Primary key
    public int MessageIndex { get; set; }
    public string? Folder { get; set; }
    public string? MoveTo { get; set; }
    public string? SenderName { get; set; }
    public string? SenderEmailaddress { get; set; }
    public DateTimeOffset Date { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
}
