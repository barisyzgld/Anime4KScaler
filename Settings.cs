using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Anime4KScaler;

public sealed class Settings
{
    public string Quality { get; set; } = "HQ";
    public int Mode { get; set; } = 1;
    public int SourceHeight { get; set; } = 720;
    public bool Enabled { get; set; } = false;
    public string Language { get; set; } = "";

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anime4KScaler", "config.json");

    static Settings Fix(Settings s)
    {
        if (s.Language != "tr" && s.Language != "en")
            s.Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
        return s;
    }

    public static Settings Load()
    {
        try { return Fix(JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()); }
        catch { var s = Fix(new Settings()); s.Save(); return s; }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
