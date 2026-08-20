using System.IO;
using System.Text.Json;

namespace Skyffle.Services;

/// <summary>Shared JSON persistence for the %LOCALAPPDATA%\Skyffle stores.</summary>
internal static class JsonStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyffle");

    // one shared instance: System.Text.Json caches serialization metadata per options object
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static T? Load<T>(string fileName) where T : class
    {
        try
        {
            string path = Path.Combine(Dir, fileName);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
            }
        }
        catch { /* corrupted store: caller falls back to defaults */ }
        return null;
    }

    public static void Save<T>(string fileName, T value)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, fileName), JsonSerializer.Serialize(value, Indented));
        }
        catch { /* non-fatal */ }
    }
}
