namespace Skyffle.Services;

public sealed class AppSettings
{
    public bool UseFahrenheit { get; set; }
}

/// <summary>Persists app settings to %LOCALAPPDATA%\Skyffle.</summary>
public static class SettingsStore
{
    public static AppSettings Load() =>
        JsonStore.Load<AppSettings>("settings.json") ?? new();

    public static void Save(AppSettings settings) =>
        JsonStore.Save("settings.json", settings);
}
