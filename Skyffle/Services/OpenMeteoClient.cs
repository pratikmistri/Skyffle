using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using Skyffle.Models;

namespace Skyffle.Services;

/// <summary>Client for the free, key-less Open-Meteo APIs.</summary>
public sealed class OpenMeteoClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    // interpolated doubles use the OS culture, which can emit comma decimals the API rejects
    private static string Coord(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    public async Task<ForecastResponse?> GetForecastAsync(double lat, double lon, bool fahrenheit = false, CancellationToken ct = default)
    {
        string url =
            "https://api.open-meteo.com/v1/forecast" +
            $"?latitude={Coord(lat)}&longitude={Coord(lon)}" +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,cloud_cover,pressure_msl,wind_speed_10m,wind_direction_10m,wind_gusts_10m" +
            "&hourly=temperature_2m,precipitation_probability,weather_code,is_day,visibility,dew_point_2m" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,uv_index_max,precipitation_probability_max,precipitation_sum" +
            "&timezone=auto&forecast_days=10" +
            (fahrenheit ? "&temperature_unit=fahrenheit" : "");
        return await Http.GetFromJsonAsync<ForecastResponse>(url, ct);
    }

    public async Task<List<GeoResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=8&language=en&format=json";
        var resp = await Http.GetFromJsonAsync<GeocodingResponse>(url, ct);
        return resp?.Results ?? [];
    }

    public async Task<double?> GetUsAqiAsync(double lat, double lon, CancellationToken ct = default)
    {
        try
        {
            string url = $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={Coord(lat)}&longitude={Coord(lon)}&current=us_aqi";
            var resp = await Http.GetFromJsonAsync<AirQualityResponse>(url, ct);
            return resp?.Current?.UsAqi;
        }
        catch
        {
            return null; // AQI is best-effort garnish
        }
    }
}
