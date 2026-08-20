using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skyffle.Models;
using Skyffle.Services;

namespace Skyffle.ViewModels;

public sealed class HourItem
{
    public string TimeLabel { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Temp { get; init; } = "";
    public string PrecipProb { get; init; } = "";
    public bool HasPrecip => PrecipProb.Length > 0;
}

public sealed class DayItem
{
    public string DayLabel { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string MinLabel { get; init; } = "";
    public string MaxLabel { get; init; } = "";
    public string PrecipProb { get; init; } = "";
    public bool HasPrecip => PrecipProb.Length > 0;
    // temperature range bar geometry against a 140px track
    public double BarOffset { get; init; }
    public double BarWidth { get; init; }
    public Microsoft.UI.Xaml.Thickness BarMargin => new(BarOffset, 0, 0, 0);
}

public sealed class DetailItem
{
    public string Title { get; init; } = "";
    public string Value { get; init; } = "";
    public string Caption { get; init; } = "";
}

public partial class MainViewModel : ObservableObject
{
    private const double BarTrack = 140;

    private readonly OpenMeteoClient api = new();
    private CancellationTokenSource? loadCts;
    private CancellationTokenSource? searchCts;

    public ObservableCollection<SavedLocation> Locations { get; } = [];
    public ObservableCollection<GeoResult> Suggestions { get; } = [];
    public ObservableCollection<HourItem> Hours { get; } = [];
    public ObservableCollection<DayItem> Days { get; } = [];
    public ObservableCollection<DetailItem> Details { get; } = [];

    [ObservableProperty] private SavedLocation? selectedLocation;
    [ObservableProperty] private string locationName = "";
    [ObservableProperty] private string currentTemp = "--°";
    [ObservableProperty] private string condition = "";
    [ObservableProperty] private string hiLo = "";
    [ObservableProperty] private string feelsLikeShort = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private bool useFahrenheit;

    /// <summary>Raised after a forecast loads so the window can drive the sky shader.</summary>
    public event Action<int, bool, double, double>? WeatherApplied;

    public MainViewModel()
    {
        // set the backing field directly so the initial load isn't triggered twice
        useFahrenheit = SettingsStore.Load().UseFahrenheit;
        // IsCurrentLocation entries are re-resolved each launch; drop any stale persisted one
        foreach (var loc in LocationStore.Load().Where(l => !l.IsCurrentLocation))
        {
            Locations.Add(loc);
        }
        if (Locations.Count == 0)
        {
            Locations.Add(new SavedLocation { Name = "Seattle", Country = "United States", Latitude = 47.6062, Longitude = -122.3321 });
            Locations.Add(new SavedLocation { Name = "London", Country = "United Kingdom", Latitude = 51.5072, Longitude = -0.1276 });
            Locations.Add(new SavedLocation { Name = "Mumbai", Country = "India", Latitude = 19.0760, Longitude = 72.8777 });
            Persist();
        }
        SelectedLocation = Locations[0];
        _ = AddCurrentLocationAsync(startupSelection: SelectedLocation);
    }

    private async Task AddCurrentLocationAsync(SavedLocation startupSelection)
    {
        var current = await GeoLocationService.TryGetCurrentAsync();
        if (current is null) return;
        Locations.Insert(0, current);
        // switch to it only if the user hasn't already picked another city meanwhile
        if (ReferenceEquals(SelectedLocation, startupSelection))
        {
            SelectedLocation = current;
        }
    }

    partial void OnSelectedLocationChanged(SavedLocation? value)
    {
        if (value is not null)
        {
            _ = LoadAsync(value);
        }
    }

    public async Task SearchAsync(string query)
    {
        // cancel the in-flight search so a slow older response can't repopulate
        // the list after a newer query's results have landed
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        Suggestions.Clear();
        if (query.Trim().Length < 2) return;
        try
        {
            var results = await api.SearchAsync(query, cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            foreach (var r in results)
            {
                Suggestions.Add(r);
            }
        }
        catch { /* cancelled or transient network issue; suggestions just stay empty */ }
    }

    public void AddLocation(GeoResult geo)
    {
        var existing = Locations.FirstOrDefault(l => !l.IsCurrentLocation &&
            Math.Abs(l.Latitude - geo.Latitude) < 0.01 && Math.Abs(l.Longitude - geo.Longitude) < 0.01);
        if (existing is null)
        {
            existing = new SavedLocation { Name = geo.Name, Country = geo.Country, Latitude = geo.Latitude, Longitude = geo.Longitude };
            Locations.Add(existing);
            Persist();
        }
        SelectedLocation = existing;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedLocation is null || Locations.Count <= 1) return;
        int idx = Locations.IndexOf(SelectedLocation);
        Locations.Remove(SelectedLocation);
        Persist();
        SelectedLocation = Locations[Math.Clamp(idx, 0, Locations.Count - 1)];
    }

    [RelayCommand]
    private void NextLocation() => CycleLocation(+1);

    [RelayCommand]
    private void PreviousLocation() => CycleLocation(-1);

    private void CycleLocation(int delta)
    {
        if (Locations.Count < 2 || SelectedLocation is null) return;
        int idx = Locations.IndexOf(SelectedLocation);
        SelectedLocation = Locations[(idx + delta + Locations.Count) % Locations.Count];
    }

    [RelayCommand]
    private void ToggleUnit() => UseFahrenheit = !UseFahrenheit;

    partial void OnUseFahrenheitChanged(bool value)
    {
        SettingsStore.Save(new AppSettings { UseFahrenheit = value });
        if (SelectedLocation is not null)
        {
            _ = LoadAsync(SelectedLocation);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedLocation is not null)
        {
            await LoadAsync(SelectedLocation);
        }
    }

    private void Persist() => LocationStore.Save(Locations.Where(l => !l.IsCurrentLocation));

    private async Task LoadAsync(SavedLocation loc)
    {
        loadCts?.Cancel();
        var cts = loadCts = new CancellationTokenSource();
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            var fcTask = api.GetForecastAsync(loc.Latitude, loc.Longitude, UseFahrenheit, cts.Token);
            var aqiTask = api.GetUsAqiAsync(loc.Latitude, loc.Longitude, cts.Token);
            var fc = await fcTask;
            double? aqi = await aqiTask;
            if (cts.Token.IsCancellationRequested || fc?.Current is null) return;
            Apply(loc, fc, aqi);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = $"Couldn't load weather — {ex.Message}";
        }
        finally
        {
            if (loadCts == cts) IsLoading = false;
        }
    }

    private void Apply(SavedLocation loc, ForecastResponse fc, double? aqi)
    {
        var cur = fc.Current!;
        bool isDay = cur.IsDay == 1;

        LocationName = loc.Name;
        CurrentTemp = $"{Math.Round(cur.Temperature)}°{(UseFahrenheit ? "F" : "C")}";
        Condition = Wmo.Describe(cur.WeatherCode);
        FeelsLikeShort = $"Feels like {Math.Round(cur.FeelsLike)}°";

        // ----- hourly: next 24 from now -----
        Hours.Clear();
        var hourly = fc.Hourly;
        if (hourly is not null && hourly.Time.Count > 0)
        {
            // cur.Time is a 15-minute step (e.g. 14:30); truncate to the hour so the
            // in-progress hour (14:00) is the "Now" slot, matching the hero reading
            var nowLocal = DateTime.Parse(cur.Time!, CultureInfo.InvariantCulture);
            var nowHour = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0);
            int start = 0;
            for (int i = 0; i < hourly.Time.Count; i++)
            {
                if (DateTime.Parse(hourly.Time[i], CultureInfo.InvariantCulture) >= nowHour) { start = i; break; }
            }
            // parallel arrays are deserialized independently; bound by every list we index
            int hourEnd = Math.Min(Math.Min(start + 24, hourly.Time.Count),
                                   Math.Min(hourly.WeatherCode.Count, hourly.Temperature.Count));
            for (int i = start; i < hourEnd; i++)
            {
                var ht = DateTime.Parse(hourly.Time[i], CultureInfo.InvariantCulture);
                double? pp = i < hourly.PrecipProbability.Count ? hourly.PrecipProbability[i] : null;
                Hours.Add(new HourItem
                {
                    TimeLabel = i == start ? "Now" : ht.ToString("h tt", CultureInfo.InvariantCulture).Replace(" ", "").ToLowerInvariant(),
                    Glyph = Wmo.Glyph(hourly.WeatherCode[i], i < hourly.IsDay.Count && hourly.IsDay[i] == 1),
                    Temp = $"{Math.Round(hourly.Temperature[i])}°",
                    PrecipProb = pp is > 15 ? $"{pp:0}%" : "",
                });
            }
        }

        // ----- 10-day with range bars -----
        Days.Clear();
        HiLo = ""; // reset so a daily-less response doesn't show the previous city's H/L
        var daily = fc.Daily;
        // parallel arrays are deserialized independently; bound by every list we index
        int dayCount = daily is null ? 0 :
            Math.Min(Math.Min(daily.Time.Count, daily.WeatherCode.Count),
                     Math.Min(daily.TempMin.Count, daily.TempMax.Count));
        if (daily is not null && dayCount > 0)
        {
            double gMin = daily.TempMin.Take(dayCount).Min();
            double gMax = daily.TempMax.Take(dayCount).Max();
            double span = Math.Max(1, gMax - gMin);
            HiLo = $"H:{Math.Round(daily.TempMax[0])}°  L:{Math.Round(daily.TempMin[0])}°";

            for (int i = 0; i < dayCount; i++)
            {
                var d = DateTime.Parse(daily.Time[i], CultureInfo.InvariantCulture);
                double? pp = i < daily.PrecipProbabilityMax.Count ? daily.PrecipProbabilityMax[i] : null;
                Days.Add(new DayItem
                {
                    DayLabel = i == 0 ? "Today" : d.ToString("ddd", CultureInfo.InvariantCulture),
                    Glyph = Wmo.Glyph(daily.WeatherCode[i], true),
                    MinLabel = $"{Math.Round(daily.TempMin[i])}°",
                    MaxLabel = $"{Math.Round(daily.TempMax[i])}°",
                    PrecipProb = pp is > 15 ? $"{pp:0}%" : "",
                    BarOffset = (daily.TempMin[i] - gMin) / span * BarTrack,
                    BarWidth = Math.Max(6, (daily.TempMax[i] - daily.TempMin[i]) / span * BarTrack),
                });
            }
        }

        // ----- detail cards -----
        Details.Clear();
        Details.Add(new DetailItem { Title = "FEELS LIKE", Value = $"{Math.Round(cur.FeelsLike)}°", Caption = FeelsCaption(cur.FeelsLike, cur.Temperature) });
        Details.Add(new DetailItem { Title = "HUMIDITY", Value = $"{cur.Humidity:0}%", Caption = DewCaption(fc) });
        Details.Add(new DetailItem { Title = "WIND", Value = $"{cur.WindSpeed:0} km/h", Caption = $"{Compass(cur.WindDirection)} · gusts {cur.WindGusts:0} km/h" });
        Details.Add(new DetailItem { Title = "PRESSURE", Value = $"{cur.Pressure:0} hPa", Caption = cur.Pressure >= 1013 ? "High" : "Low" });
        if (daily is not null && daily.UvIndexMax.Count > 0 && daily.UvIndexMax[0] is double uv)
        {
            Details.Add(new DetailItem { Title = "UV INDEX", Value = $"{uv:0}", Caption = UvCaption(uv) });
        }
        if (TryVisibility(fc) is double vis)
        {
            Details.Add(new DetailItem { Title = "VISIBILITY", Value = vis >= 1000 ? $"{vis / 1000:0.#} km" : $"{vis:0} m", Caption = vis >= 10000 ? "Perfectly clear" : vis >= 4000 ? "Good" : "Reduced" });
        }
        if (daily is not null && daily.Sunrise.Count > 0 && daily.Sunset.Count > 0)
        {
            var sr = DateTime.Parse(daily.Sunrise[0], CultureInfo.InvariantCulture);
            var ss = DateTime.Parse(daily.Sunset[0], CultureInfo.InvariantCulture);
            Details.Add(new DetailItem { Title = "SUNRISE", Value = sr.ToString("h:mm tt", CultureInfo.InvariantCulture), Caption = $"Sunset {ss.ToString("h:mm tt", CultureInfo.InvariantCulture)}" });
        }
        if (daily is not null && daily.PrecipitationSum.Count > 0 && daily.PrecipitationSum[0] is double ps)
        {
            Details.Add(new DetailItem { Title = "PRECIPITATION", Value = $"{ps:0.#} mm", Caption = "expected today" });
        }
        if (aqi is double a)
        {
            Details.Add(new DetailItem { Title = "AIR QUALITY", Value = $"{a:0}", Caption = AqiCaption(a) });
        }

        WeatherApplied?.Invoke(cur.WeatherCode, isDay, cur.CloudCover, cur.WindSpeed);
    }

    private static double? TryVisibility(ForecastResponse fc)
    {
        var h = fc.Hourly;
        if (h?.Visibility is { Count: > 0 } v && v[0] is double first) return first;
        return null;
    }

    private static string DewCaption(ForecastResponse fc)
    {
        var h = fc.Hourly;
        if (h?.DewPoint is { Count: > 0 } dp && dp[0] is double d) return $"Dew point {Math.Round(d)}°";
        return "";
    }

    private static string FeelsCaption(double feels, double actual) =>
        Math.Abs(feels - actual) < 1.5 ? "Similar to actual" :
        feels > actual ? "Humidity makes it feel warmer" : "Wind makes it feel cooler";

    private static string UvCaption(double uv) => uv switch
    {
        < 3 => "Low", < 6 => "Moderate", < 8 => "High", < 11 => "Very High", _ => "Extreme",
    };

    private static string AqiCaption(double aqi) => aqi switch
    {
        <= 50 => "Good", <= 100 => "Moderate", <= 150 => "Unhealthy (sensitive)", <= 200 => "Unhealthy", _ => "Hazardous",
    };

    private static string Compass(double deg)
    {
        string[] dirs = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return dirs[(int)Math.Round(deg / 45.0) % 8];
    }
}
