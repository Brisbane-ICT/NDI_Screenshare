using System.Text.Json;

namespace NdiScreenshare;

public sealed class AppConfig
{
    public string DisplayDeviceName { get; set; } = "";
    public int Fps { get; set; } = 20;
    public string SourceName { get; set; } = "SCREENSHARE";
    public double Scale { get; set; } = 1.0;
    public bool AutoStart { get; set; } = true;
    public bool HideCursor { get; set; } = true;
    public bool Running { get; set; } = true;

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NDI_Screenshare", "config.json");

    public static AppConfig Load()
    {
        try
        {
            var p = ConfigPath;
            if (File.Exists(p))
            {
                var json = File.ReadAllText(p);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var p = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, p, true);
        }
        catch { }
    }
}
