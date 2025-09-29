using System.Text.Json;

namespace SwitchMonitoring.Services;

public class UserPreferences
{
    public int HistoricalWindowMinutes { get; set; } = 60; // default 1 hour
    public bool SmoothingEnabled { get; set; } = true;
    public bool FuturePaddingEnabled { get; set; } = false;

    private static readonly string PrefsFile = Path.Combine(AppContext.BaseDirectory, "userprefs.json");
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

    public static UserPreferences Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(PrefsFile))
            {
                var json = File.ReadAllText(PrefsFile);
                var prefs = JsonSerializer.Deserialize<UserPreferences>(json, JsonOptions);
                if (prefs != null)
                {
                    Current = prefs;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to load user preferences: {ex.Message}");
        }
    }

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(PrefsFile, json);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to save user preferences: {ex.Message}");
        }
    }
}
