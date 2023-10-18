using System.Data;
using ServiceStack.OrmLite;

namespace MailZort;

internal class MailDb
{
    private readonly SettingsHelper _settingsHelper;

    public MailDb(SettingsHelper settingsHelper)
    {
        string path = Path.Combine(settingsHelper.SettingPath, @"mail.db");

        OrmLiteConnectionFactory db = new(path, SqliteDialect.Provider);

        using IDbConnection dbc = db.Open();

        _ = dbc.CreateTableIfNotExists<Email>();

        _settingsHelper = settingsHelper;
    }

    public IDbConnection GetConnection()
    {
        string path = Path.Combine(_settingsHelper.SettingPath, @"mail.db");

        OrmLiteConnectionFactory db = new(path, SqliteDialect.Provider);

        return db.Open();
    }
}
