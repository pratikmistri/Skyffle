using Skyffle.Models;

namespace Skyffle.Services;

/// <summary>Persists the user's saved locations to %LOCALAPPDATA%\Skyffle.</summary>
public static class LocationStore
{
    public static List<SavedLocation> Load() =>
        JsonStore.Load<List<SavedLocation>>("locations.json") ?? [];

    public static void Save(IEnumerable<SavedLocation> locations) =>
        JsonStore.Save("locations.json", locations.ToList());
}
