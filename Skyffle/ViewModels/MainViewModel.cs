using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skyffle.Models;
using Skyffle.Services;
using Windows.UI;

namespace Skyffle.ViewModels;

public sealed class HourItem
{
    public string TimeLabel { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Temp { get; init; } = "";
    public string PrecipProb { get; init; } = "";
    public bool HasPrecip => PrecipProb.Length > 0;
}

/// <summary>One hour of a day's curve: the numbers the chart plots, and the strings it reads out on hover.</summary>
public sealed class HourPoint
{
    public DateTime Time { get; init; }
    public double Temp { get; init; }
    public double? PrecipProbability { get; init; }
    public string Glyph { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>The hour in progress — true on today's row only; the chart marks it on the curve.</summary>
    public bool IsNow { get; init; }
}

public sealed partial class DayItem : ObservableObject
{
    public string DayLabel { get; init; } = "";
    public string DateLabel { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string MinLabel { get; init; } = "";
    public string MaxLabel { get; init; } = "";
    public string PrecipProb { get; init; } = "";
    public bool HasPrecip => PrecipProb.Length > 0;
    // temperature range bar geometry against a 140px track
    public double BarOffset { get; init; }
    public double BarWidth { get; init; }
    public Microsoft.UI.Xaml.Thickness BarMargin => new(BarOffset, 0, 0, 0);

    /// <summary>This day's hours, plotted by the chart while the row is expanded.</summary>
    public IReadOnlyList<HourPoint> Hours { get; init; } = [];
    public bool HasHours => Hours.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool isExpanded;

    /// <summary>Chevron down when closed, up when open.</summary>
    public string ExpandGlyph => IsExpanded ? "" : "";
}

public sealed class DetailItem
{
    public string Title { get; init; } = "";
    public string Value { get; init; } = "";
    public string Caption { get; init; } = "";

    /// <summary>Set only on the cards whose number means more as a position on a printed
    /// face than as a bare figure. Null leaves the card as plain text.</summary>
    public Controls.GaugeSpec? Gauge { get; init; }

    public Microsoft.UI.Xaml.Visibility GaugeVisibility =>
        Gauge is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility PlainVisibility =>
        Gauge is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
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
    [ObservableProperty] private string tempUnit = "";
    [ObservableProperty] private string condition = "";
    [ObservableProperty] private string hiLo = "";
    [ObservableProperty] private string feelsLikeShort = "";
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private bool useFahrenheit;

    /// <summary>Raised after a forecast loads so the window can drive the sky shader.</summary>
    public event Action<Graphics.SkyConditions>? WeatherApplied;

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
        CurrentTemp = $"{Math.Round(cur.Temperature)}°";
        TempUnit = UseFahrenheit ? "F" : "C";
        Condition = Wmo.Describe(cur.WeatherCode);
        FeelsLikeShort = $"Feels like {Math.Round(cur.FeelsLike)}°";

        // cur.Time is a 15-minute step (e.g. 14:30); truncate to the hour so the
        // in-progress hour (14:00) is the "Now" slot, matching the hero reading
        var nowLocal = DateTime.Parse(cur.Time!, CultureInfo.InvariantCulture);
        var nowHour = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0);

        // ----- hourly: next 24 from now -----
        Hours.Clear();
        var hourly = fc.Hourly;
        if (hourly is not null && hourly.Time.Count > 0)
        {
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

        // ----- 10-day with range bars, each row expandable into its own hourly curve -----
        Days.Clear();
        HiLo = ""; // reset so a daily-less response doesn't show the previous city's H/L
        var curves = BuildDayCurves(hourly, nowHour);
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
                string summary = Wmo.Describe(daily.WeatherCode[i]);
                if (pp is > 15) summary += $" · {pp:0}% chance of precipitation";
                Days.Add(new DayItem
                {
                    DayLabel = i == 0 ? "Today" : d.ToString("ddd", CultureInfo.InvariantCulture),
                    DateLabel = d.ToString("dddd, MMMM d", CultureInfo.InvariantCulture),
                    Summary = summary,
                    Glyph = Wmo.Glyph(daily.WeatherCode[i], true),
                    MinLabel = $"{Math.Round(daily.TempMin[i])}°",
                    MaxLabel = $"{Math.Round(daily.TempMax[i])}°",
                    PrecipProb = pp is > 15 ? $"{pp:0}%" : "",
                    BarOffset = (daily.TempMin[i] - gMin) / span * BarTrack,
                    BarWidth = Math.Max(6, (daily.TempMax[i] - daily.TempMin[i]) / span * BarTrack),
                    Hours = curves.TryGetValue(DateOnly.FromDateTime(d), out var curve) ? curve : [],
                });
            }
        }

        // ----- detail cards -----
        Details.Clear();
        Details.Add(new DetailItem { Title = "FEELS LIKE", Value = $"{Math.Round(cur.FeelsLike)}°", Caption = FeelsCaption(cur.FeelsLike, cur.Temperature) });
        Details.Add(new DetailItem { Title = "HUMIDITY", Value = $"{cur.Humidity:0}%", Caption = DewCaption(fc) });
        // the arrow carries the direction, so the caption only has to name it in words
        Details.Add(new DetailItem
        {
            Title = "WIND",
            Value = $"{cur.WindSpeed:0}",
            Caption = $"from {Compass(cur.WindDirection)} · gusts {cur.WindGusts:0} km/h",
            Gauge = new Controls.GaugeSpec
            {
                Kind = Controls.GaugeKind.Compass,
                Fraction = cur.WindDirection / 360.0,
                UnitLabel = "km/h",
            },
        });
        // no bands: pressure has no hazard categories to colour, only a low end and a high
        // end, so it gets a plain tick comb and the unit tucks under the reading
        Details.Add(new DetailItem
        {
            Title = "PRESSURE",
            Value = $"{cur.Pressure:0}",
            Caption = PressureCaption(cur.Pressure),
            Gauge = new Controls.GaugeSpec
            {
                Kind = Controls.GaugeKind.Dial,
                Fraction = Math.Clamp((cur.Pressure - 980) / 60.0, 0, 1),
                MinLabel = "Low",
                MaxLabel = "High",
                UnitLabel = "hPa",
            },
        });
        if (daily is not null && daily.UvIndexMax.Count > 0 && daily.UvIndexMax[0] is double uv)
        {
            Details.Add(new DetailItem
            {
                Title = "UV INDEX",
                Value = $"{uv:0}",
                Caption = UvCaption(uv),
                Gauge = Face(uv, 0, 12, UvBands, "0", "11+"),
            });
        }
        if (TryVisibility(fc) is double vis)
        {
            Details.Add(new DetailItem { Title = "VISIBILITY", Value = vis >= 1000 ? $"{vis / 1000:0.#} km" : $"{vis:0} m", Caption = vis >= 10000 ? "Perfectly clear" : vis >= 4000 ? "Good" : "Reduced" });
        }
        if (daily is not null && daily.Sunrise.Count > 0 && daily.Sunset.Count > 0)
        {
            var sr = DateTime.Parse(daily.Sunrise[0], CultureInfo.InvariantCulture);
            var ss = DateTime.Parse(daily.Sunset[0], CultureInfo.InvariantCulture);
            Details.Add(new DetailItem
            {
                Title = "SUNRISE",
                Value = sr.ToString("h:mm tt", CultureInfo.InvariantCulture),
                Caption = $"Sunset {ss.ToString("h:mm tt", CultureInfo.InvariantCulture)}",
                Gauge = DayArc(sr, ss, cur.Time is null ? sr : DateTime.Parse(cur.Time, CultureInfo.InvariantCulture)),
            });
        }
        if (daily is not null && daily.PrecipitationSum.Count > 0 && daily.PrecipitationSum[0] is double ps)
        {
            Details.Add(new DetailItem { Title = "PRECIPITATION", Value = $"{ps:0.#} mm", Caption = "expected today" });
        }
        if (aqi is double a)
        {
            Details.Add(new DetailItem
            {
                Title = "AIR QUALITY",
                Value = $"{a:0}",
                Caption = AqiCaption(a),
                Gauge = Face(a, 0, 300, AqiBands, "0", "300", upperInclusive: true),
            });
        }

        // sun: fraction of the way from sunrise (0) to sunset (1);
        // moon: fraction of the night from sunset (0) to the next sunrise (1)
        double sunProgress = 0.5;
        double moonProgress = 0.5;
        if (daily is not null && daily.Sunrise.Count > 0 && daily.Sunset.Count > 0 && cur.Time is not null)
        {
            var sr = DateTime.Parse(daily.Sunrise[0], CultureInfo.InvariantCulture);
            var ss = DateTime.Parse(daily.Sunset[0], CultureInfo.InvariantCulture);
            var now = DateTime.Parse(cur.Time, CultureInfo.InvariantCulture);
            double spanMin = (ss - sr).TotalMinutes;
            if (spanMin > 1)
            {
                sunProgress = Math.Clamp((now - sr).TotalMinutes / spanMin, 0.0, 1.0);
            }
            if (now < sr)
            {
                // pre-dawn: yesterday's sunset is a minute or two off today's — close enough
                var prevSet = ss.AddDays(-1);
                moonProgress = (now - prevSet).TotalMinutes / (sr - prevSet).TotalMinutes;
            }
            else if (now > ss)
            {
                var nextRise = daily.Sunrise.Count > 1
                    ? DateTime.Parse(daily.Sunrise[1], CultureInfo.InvariantCulture)
                    : sr.AddDays(1);
                moonProgress = (now - ss).TotalMinutes / Math.Max(1, (nextRise - ss).TotalMinutes);
            }
            moonProgress = Math.Clamp(moonProgress, 0.0, 1.0);
        }

        // synodic phase from a reference new moon (2000-01-06 18:14 UTC): 0 new → 0.5 full
        const double SynodicDays = 29.530588853;
        double moonPhase = ((DateTime.UtcNow - new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc)).TotalDays
                            % SynodicDays) / SynodicDays;

        WeatherApplied?.Invoke(new Graphics.SkyConditions(
            cur.WeatherCode, isDay, cur.CloudCover, cur.WindSpeed, sunProgress, moonProgress, moonPhase));
    }

    /// <summary>
    /// Buckets the flat hourly arrays by calendar day so any row in the 10-day list can
    /// plot its own 24-hour curve without another request — the forecast already carries
    /// every hour of the ten days it returns.
    /// </summary>
    private static Dictionary<DateOnly, List<HourPoint>> BuildDayCurves(HourlyBlock? hourly, DateTime nowHour)
    {
        var curves = new Dictionary<DateOnly, List<HourPoint>>();
        if (hourly is null) return curves;
        // parallel arrays are deserialized independently; bound by every list we index
        int count = Math.Min(Math.Min(hourly.Time.Count, hourly.Temperature.Count), hourly.WeatherCode.Count);
        for (int i = 0; i < count; i++)
        {
            var t = DateTime.Parse(hourly.Time[i], CultureInfo.InvariantCulture);
            var key = DateOnly.FromDateTime(t);
            if (!curves.TryGetValue(key, out var list))
            {
                curves[key] = list = new List<HourPoint>(24);
            }
            list.Add(new HourPoint
            {
                Time = t,
                Temp = hourly.Temperature[i],
                PrecipProbability = i < hourly.PrecipProbability.Count ? hourly.PrecipProbability[i] : null,
                Glyph = Wmo.Glyph(hourly.WeatherCode[i], i < hourly.IsDay.Count && hourly.IsDay[i] == 1),
                Description = Wmo.Describe(hourly.WeatherCode[i]),
                IsNow = t == nowHour,
            });
        }
        return curves;
    }

    /// <summary>Accordion: opening one day's curve closes whichever was open.</summary>
    public void ToggleDay(DayItem day)
    {
        bool opening = !day.IsExpanded && day.HasHours;
        foreach (var d in Days)
        {
            d.IsExpanded = false;
        }
        day.IsExpanded = opening;
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

    private static string PressureCaption(double hPa) =>
        hPa < 1000 ? "Low" : hPa < 1020 ? "Steady" : "High";

    // ----- gauge faces -----
    //
    // Each band table is the same threshold list the caption above it uses, so the band the
    // marker lands in is always the band the caption names.
    //
    // The ramp is the app's own: it starts on the sky blue that opens the temperature range
    // bars (#6EC1FF), passes through the amber that closes them (#FFD36E), and ends near the
    // coral the hero already uses for "feels like" (#FFB3AB). The usual hazard green and
    // violet are deliberately absent — neither hue appears anywhere else in Skyffle.

    private static readonly Color BandLow = Color.FromArgb(0xFF, 0x6E, 0xC1, 0xFF);
    private static readonly Color BandMid = Color.FromArgb(0xFF, 0xFF, 0xD3, 0x6E);
    private static readonly Color BandHigh = Color.FromArgb(0xFF, 0xFF, 0xA9, 0x6B);
    private static readonly Color BandSevere = Color.FromArgb(0xFF, 0xFF, 0x8A, 0x7A);
    private static readonly Color BandExtreme = Color.FromArgb(0xFF, 0xE5, 0x67, 0x7F);

    private static readonly (double Upper, Color Color)[] UvBands =
        [(3, BandLow), (6, BandMid), (8, BandHigh), (11, BandSevere), (12, BandExtreme)];

    private static readonly (double Upper, Color Color)[] AqiBands =
        [(50, BandLow), (100, BandMid), (150, BandHigh), (200, BandSevere), (300, BandExtreme)];

    /// <summary>
    /// Builds a face from a band table. The band the reading falls in is drawn at full
    /// strength and the rest are dimmed, so "which band am I in" lands before the number does.
    /// </summary>
    private static Controls.GaugeSpec Face(double value, double min, double max,
        (double Upper, Color Color)[] bands, string minLabel, string maxLabel, bool upperInclusive = false)
    {
        int active = bands.Length - 1;
        for (int i = 0; i < bands.Length; i++)
        {
            if (upperInclusive ? value <= bands[i].Upper : value < bands[i].Upper) { active = i; break; }
        }

        var zones = new List<Controls.GaugeZone>(bands.Length);
        double at = min;
        foreach (var (upper, color) in bands)
        {
            double top = Math.Clamp(upper, min, max);
            zones.Add(new Controls.GaugeZone
            {
                Extent = Math.Max(0, top - at),
                Color = Alpha(color, zones.Count == active ? (byte)0xFF : (byte)0x4D),
            });
            at = top;
        }

        return new Controls.GaugeSpec
        {
            Fraction = Math.Clamp((value - min) / (max - min), 0, 1),
            Zones = zones,
            MinLabel = minLabel,
            MaxLabel = maxLabel,
        };
    }

    /// <summary>
    /// The sun's path across the card: daylight is the stretch of curve above the horizon,
    /// and the margin either side is how far the sun has sunk below it. Showing a slice of
    /// night at both ends is what makes sunrise and sunset visible as the two crossings.
    /// </summary>
    private const double NightMargin = 0.2;

    private static Controls.GaugeSpec DayArc(DateTime sunrise, DateTime sunset, DateTime now)
    {
        // the drawn window is one daylight span plus a margin of night at each end
        const double window = 1 + 2 * NightMargin;
        double dayLength = (sunset - sunrise).TotalMinutes;
        double throughDay = dayLength > 1 ? (now - sunrise).TotalMinutes / dayLength : 0.5;
        return new Controls.GaugeSpec
        {
            Kind = Controls.GaugeKind.DayCurve,
            RiseFraction = NightMargin / window,
            SetFraction = (1 + NightMargin) / window,
            Fraction = Math.Clamp((NightMargin + throughDay) / window, 0, 1),
        };
    }

    private static Color Alpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static string Compass(double deg)
    {
        string[] dirs = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return dirs[(int)Math.Round(deg / 45.0) % 8];
    }
}
