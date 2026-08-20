using System.Net.Http;
using System.Net.Http.Json;
using Windows.Devices.Geolocation;
using Windows.Services.Maps;
using Skyffle.Models;

namespace Skyffle.Services;

/// <summary>
/// Resolves the device's current position via Windows geolocation and reverse-geocodes
/// it to a city name — first with Windows.Services.Maps, then falling back to the free
/// key-less BigDataCloud API. Returns null when location access is denied
/// (Settings → Privacy &amp; security → Location) or the position can't be fixed.
/// </summary>
public static class GeoLocationService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

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

            var loc = new SavedLocation
            {
                Name = "My Location",
                IsCurrentLocation = true,
                Latitude = point.Position.Latitude,
                Longitude = point.Position.Longitude,
            };

            if (!TryResolveNameWithMaps(loc, await ReverseGeocodeViaMapsAsync(point)))
            {
                await ResolveNameViaHttpAsync(loc);
            }
            return loc;
        }
        catch
        {
            return null; // capability missing, access revoked mid-call, or position timeout
        }
    }

    private static async Task<MapLocationFinderResult?> ReverseGeocodeViaMapsAsync(Geopoint point)
    {
        try
        {
            return await MapLocationFinder.FindLocationsAtAsync(point);
        }
        catch
        {
            return null; // maps service unavailable
        }
    }

    private static bool TryResolveNameWithMaps(SavedLocation loc, MapLocationFinderResult? result)
    {
        if (result?.Status != MapLocationFinderStatus.Success || result.Locations.Count == 0)
        {
            return false;
        }
        var addr = result.Locations[0].Address;
        string city = FirstNonEmpty(addr.Town, addr.District, addr.Region);
        if (city.Length == 0) return false;
        loc.Name = city;
        if (addr.Country is { Length: > 0 }) loc.Country = addr.Country;
        return true;
    }

    private static async Task ResolveNameViaHttpAsync(SavedLocation loc)
    {
        try
        {
            string url = "https://api.bigdatacloud.net/data/reverse-geocode-client" +
                $"?latitude={loc.Latitude:0.####}&longitude={loc.Longitude:0.####}&localityLanguage=en";
            var resp = await Http.GetFromJsonAsync<ReverseGeocodeResponse>(url);
            if (resp is null) return;
            string city = FirstNonEmpty(resp.City, resp.Locality, resp.PrincipalSubdivision);
            if (city.Length > 0) loc.Name = city;
            if (resp.CountryName is { Length: > 0 }) loc.Country = resp.CountryName;
        }
        catch { /* offline or service down: keep the generic label */ }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
