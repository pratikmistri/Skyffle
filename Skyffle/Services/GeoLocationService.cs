using Windows.Devices.Geolocation;
using Skyffle.Models;

namespace Skyffle.Services;

/// <summary>
/// Resolves the device's current position via Windows geolocation. The entry is labeled
/// "Your location" rather than a reverse-geocoded city name: on machines without Wi-Fi or
/// GPS the fix is IP-based and only city-level accurate, so a resolved name can be wrong
/// (and can duplicate a saved city with slightly different coordinates). Returns null when
/// location access is denied (Settings → Privacy &amp; security → Location) or the position
/// can't be fixed.
/// </summary>
public static class GeoLocationService
{
    public static async Task<SavedLocation?> TryGetCurrentAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed)
            {
                return null;
            }

            var locator = new Geolocator { DesiredAccuracyInMeters = 500 };
            var pos = await locator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(10), timeout: TimeSpan.FromSeconds(15));
            var point = pos.Coordinate.Point;

            return new SavedLocation
            {
                Name = "Your location",
                IsCurrentLocation = true,
                Latitude = point.Position.Latitude,
                Longitude = point.Position.Longitude,
            };
        }
        catch
        {
            return null; // capability missing, access revoked mid-call, or position timeout
        }
    }
}
