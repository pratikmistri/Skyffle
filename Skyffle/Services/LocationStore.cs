using System.IO;
using System.Text.Json;
using Skyffle.Models;

namespace Skyffle.Services;

/// <summary>Persists the user's saved locations to %LOCALAPPDATA%\Skyffle.</summary>
public static class LocationStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyffle");
    private static readonly string FilePath = Path.Combine(Dir, "locations.json");

    public static List<SavedLocation> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<List<SavedLocation>>(File.ReadAllText(FilePath)) ?? [];
            }
        }
        catch { /* corrupted store: fall through to defaults */ }
        return [];
    }

    public static void Save(IEnumerable<SavedLocation> locations)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(locations.ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
