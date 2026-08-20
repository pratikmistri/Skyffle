using System.Text.Json.Serialization;

namespace Skyffle.Models;

// ---------- Open-Meteo DTOs ----------

public sealed class ForecastResponse
{
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
    [JsonPropertyName("timezone")] public string? Timezone { get; set; }
    [JsonPropertyName("utc_offset_seconds")] public int UtcOffsetSeconds { get; set; }
    [JsonPropertyName("current")] public CurrentBlock? Current { get; set; }
    [JsonPropertyName("hourly")] public HourlyBlock? Hourly { get; set; }
    [JsonPropertyName("daily")] public DailyBlock? Daily { get; set; }
}

public sealed class CurrentBlock
{
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("temperature_2m")] public double Temperature { get; set; }
    [JsonPropertyName("relative_humidity_2m")] public double Humidity { get; set; }
    [JsonPropertyName("apparent_temperature")] public double FeelsLike { get; set; }
    [JsonPropertyName("is_day")] public int IsDay { get; set; }
    [JsonPropertyName("weather_code")] public int WeatherCode { get; set; }
    [JsonPropertyName("cloud_cover")] public double CloudCover { get; set; }
    [JsonPropertyName("pressure_msl")] public double Pressure { get; set; }
    [JsonPropertyName("wind_speed_10m")] public double WindSpeed { get; set; }
    [JsonPropertyName("wind_direction_10m")] public double WindDirection { get; set; }
    [JsonPropertyName("wind_gusts_10m")] public double WindGusts { get; set; }
}

public sealed class HourlyBlock
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("temperature_2m")] public List<double> Temperature { get; set; } = [];
    [JsonPropertyName("precipitation_probability")] public List<double?> PrecipProbability { get; set; } = [];
    [JsonPropertyName("weather_code")] public List<int> WeatherCode { get; set; } = [];
    [JsonPropertyName("is_day")] public List<int> IsDay { get; set; } = [];
    [JsonPropertyName("visibility")] public List<double?> Visibility { get; set; } = [];
    [JsonPropertyName("dew_point_2m")] public List<double?> DewPoint { get; set; } = [];
}

public sealed class DailyBlock
{
    [JsonPropertyName("time")] public List<string> Time { get; set; } = [];
    [JsonPropertyName("weather_code")] public List<int> WeatherCode { get; set; } = [];
    [JsonPropertyName("temperature_2m_max")] public List<double> TempMax { get; set; } = [];
    [JsonPropertyName("temperature_2m_min")] public List<double> TempMin { get; set; } = [];
    [JsonPropertyName("sunrise")] public List<string> Sunrise { get; set; } = [];
    [JsonPropertyName("sunset")] public List<string> Sunset { get; set; } = [];
    [JsonPropertyName("uv_index_max")] public List<double?> UvIndexMax { get; set; } = [];
    [JsonPropertyName("precipitation_probability_max")] public List<double?> PrecipProbabilityMax { get; set; } = [];
    [JsonPropertyName("precipitation_sum")] public List<double?> PrecipitationSum { get; set; } = [];
}

public sealed class GeocodingResponse
{
    [JsonPropertyName("results")] public List<GeoResult>? Results { get; set; }
}

public sealed class GeoResult
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("latitude")] public double Latitude { get; set; }
    [JsonPropertyName("longitude")] public double Longitude { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("admin1")] public string? Admin1 { get; set; }

    public string Display => Admin1 is { Length: > 0 } a && a != Name
        ? $"{Name}, {a}, {Country}"
        : $"{Name}, {Country}";
}

public sealed class AirQualityResponse
{
    [JsonPropertyName("current")] public AirQualityCurrent? Current { get; set; }
}

public sealed class AirQualityCurrent
{
    [JsonPropertyName("us_aqi")] public double? UsAqi { get; set; }
}

// ---------- App domain ----------

public sealed class SavedLocation
{
    public string Name { get; set; } = "";
    public string? Country { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Device-derived entry; re-resolved each launch, never persisted.</summary>
    public bool IsCurrentLocation { get; set; }
}

public static class Wmo
{
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly Clear",
        2 => "Partly Cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing Drizzle",
        61 => "Light Rain",
        63 => "Rain",
        65 => "Heavy Rain",
        66 or 67 => "Freezing Rain",
        71 => "Light Snow",
        73 => "Snow",
        75 => "Heavy Snow",
        77 => "Snow Grains",
        80 => "Light Showers",
        81 => "Showers",
        82 => "Violent Showers",
        85 => "Snow Showers",
        86 => "Heavy Snow Showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm + Hail",
        _ => "—",
    };

    public static string Glyph(int code, bool isDay) => code switch
    {
        0 or 1 => isDay ? "☀️" : "🌙",
        2 => isDay ? "⛅" : "☁️",
        3 => "☁️",
        45 or 48 => "🌫️",
        51 or 53 or 55 or 56 or 57 => "🌦️",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "🌧️",
        71 or 73 or 75 or 77 or 85 or 86 => "🌨️",
        95 or 96 or 99 => "⛈️",
        _ => "🌡️",
    };
}
