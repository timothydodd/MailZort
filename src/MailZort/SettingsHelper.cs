namespace MailZort;

public class SettingsHelper
{
    public string SettingPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");


    public SettingsHelper()
    {

        if (!Directory.Exists(SettingPath))
        {
            _ = Directory.CreateDirectory(SettingPath);
        }
        string rootConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        string configFile = Path.Combine(SettingPath, "appsettings.json");


        if (!File.Exists(configFile))
        {
            Console.WriteLine($"Creating Config File {configFile}");
            File.Copy(rootConfig, configFile);
        }
    }
}
