using ComputeSharp;

namespace Skyffle.Graphics;

/// <summary>
/// CPU-side driver for <see cref="SkyShader"/>: maps WMO weather codes to shader
/// parameters and eases the live values toward them so condition changes cross-fade.
/// Written from the UI thread, read from Win2D's render thread (float writes are atomic).
/// </summary>
public sealed class SkyState
{
    private readonly Random rng = new();
    private double flashTimer;

    // targets (set from weather data)
    public float TargetDaylight = 1f;
    public float TargetCloud;
    public float TargetRain;
    public float TargetSnow;
    public float TargetFog;
    public float TargetWind;
    public bool Storm;

    // UI card rectangles (scene pixels) rain lands on; written from the UI thread
    public float4 CardA;
    public float4 CardB;
    public float4 CardC;
    public float4 CardD;
    public float4 DetailsGrid; // x, y, cellW, cellH of the detail-card grid
    public float4 DetailsMeta; // columns, item count, cell inner margin

    // live values fed to the shader
    public float Daylight = 1f;
    public float Cloud;
    public float Rain;
    public float Snow;
    public float Fog;
    public float Wind;
    public float Lightning;

    public void ApplyWeather(int wmoCode, bool isDay, double cloudCoverPercent, double windKmh)
    {
        TargetDaylight = isDay ? 1f : 0f;
        TargetCloud = (float)(cloudCoverPercent / 100.0);
        TargetWind = (float)Math.Clamp(windKmh / 60.0, 0, 1);
        TargetRain = 0f;
        TargetSnow = 0f;
        TargetFog = 0f;
        Storm = false;

        switch (wmoCode)
        {
            case 0: TargetCloud = Math.Min(TargetCloud, 0.05f); break;
            case 1: TargetCloud = Math.Max(TargetCloud, 0.15f); break;
            case 2: TargetCloud = Math.Max(TargetCloud, 0.45f); break;
            case 3: TargetCloud = Math.Max(TargetCloud, 0.95f); break;
            case 45 or 48: TargetFog = 0.85f; TargetCloud = Math.Max(TargetCloud, 0.6f); break;
            case 51 or 56: TargetRain = 0.25f; TargetCloud = Math.Max(TargetCloud, 0.7f); break;
            case 53 or 57: TargetRain = 0.40f; TargetCloud = Math.Max(TargetCloud, 0.8f); break;
            case 55: TargetRain = 0.55f; TargetCloud = Math.Max(TargetCloud, 0.85f); break;
            case 61 or 80: TargetRain = 0.45f; TargetCloud = Math.Max(TargetCloud, 0.8f); break;
            case 63 or 81: TargetRain = 0.70f; TargetCloud = Math.Max(TargetCloud, 0.9f); break;
            case 65 or 82: TargetRain = 1.00f; TargetCloud = Math.Max(TargetCloud, 1.0f); break;
            case 66 or 67: TargetRain = 0.60f; TargetSnow = 0.25f; TargetCloud = Math.Max(TargetCloud, 0.9f); break;
            case 71 or 85: TargetSnow = 0.45f; TargetCloud = Math.Max(TargetCloud, 0.75f); break;
            case 73: TargetSnow = 0.70f; TargetCloud = Math.Max(TargetCloud, 0.85f); break;
            case 75 or 86: TargetSnow = 1.00f; TargetCloud = Math.Max(TargetCloud, 0.95f); break;
            case 77: TargetSnow = 0.35f; TargetCloud = Math.Max(TargetCloud, 0.7f); break;
            case 95 or 96 or 99:
                TargetRain = 0.85f; TargetCloud = 1.0f; Storm = true; break;
        }
    }

    /// <summary>Advance eased values; called once per rendered frame on the render thread.</summary>
    public void Step(double dt)
    {
        float k = (float)Math.Min(1.0, dt * 0.8); // ~1.25 s cross-fade
        Daylight += (TargetDaylight - Daylight) * k;
        Cloud += (TargetCloud - Cloud) * k;
        Rain += (TargetRain - Rain) * k;
        Snow += (TargetSnow - Snow) * k;
        Fog += (TargetFog - Fog) * k;
        Wind += (TargetWind - Wind) * k;

        // lightning: random double-flicker bursts during storms, exponential decay
        Lightning *= (float)Math.Exp(-dt * 9.0);
        if (Storm)
        {
            flashTimer -= dt;
            if (flashTimer <= 0)
            {
                Lightning = 0.35f + (float)rng.NextDouble() * 0.45f;
                // occasionally a quick follow-up flash, otherwise a long gap
                flashTimer = rng.NextDouble() < 0.35 ? 0.12 + rng.NextDouble() * 0.2
                                                     : 2.5 + rng.NextDouble() * 6.0;
            }
        }
    }
}
