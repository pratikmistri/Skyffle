using ComputeSharp;

namespace Skyffle.Graphics;

/// <summary>Everything the sky shader needs about the applied forecast.</summary>
/// <param name="SunProgress01">0 at sunrise → 1 at sunset along the day arc.</param>
/// <param name="MoonProgress01">0 at sunset → 1 at the next sunrise along the night arc.</param>
/// <param name="MoonPhase01">Synodic phase: 0 new → 0.5 full → 1 new again.</param>
public readonly record struct SkyConditions(
    int WmoCode, bool IsDay, double CloudCoverPercent, double WindKmh,
    double SunProgress01, double MoonProgress01, double MoonPhase01);

/// <summary>
/// CPU-side driver for <see cref="SkyShader"/>: maps WMO weather codes to shader
/// parameters and eases the live values toward them so condition changes cross-fade.
/// Written from the UI thread, read from Win2D's render thread (float writes are atomic).
/// </summary>
public sealed class SkyState
{
    private readonly Random rng = new();
    private double flashTimer;
    private double boltTimer;    // gap until the current strike's next return stroke
    private int boltStrokesLeft; // return strokes still to fire in the current strike

    // targets (set from weather data)
    public float TargetDaylight = 1f;
    public float TargetCloud;
    public float TargetRain;
    public float TargetSnow;
    public float TargetFog;
    public float TargetWind;
    public float TargetSunProgress = 0.5f;  // 0 sunrise → 1 sunset
    public float TargetMoonProgress = 0.5f; // 0 sunset → 1 next sunrise
    public bool Storm;
    public bool AlwaysBolt; // debug hook: makes every lightning event a drawn strike

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
    public float Bolt;     // brightness of a drawn strike; 0 for the far commoner sheet flash
    public float BoltSeed; // reshuffled per strike: picks its position and jagged path
    public float SunProgress = 0.5f;
    public float MoonProgress = 0.5f;
    public float MoonPhase = 0.5f; // not eased: it moves ~3% a day, a jump is invisible

    public void ApplyWeather(in SkyConditions c)
    {
        TargetDaylight = c.IsDay ? 1f : 0f;
        TargetSunProgress = (float)Math.Clamp(c.SunProgress01, 0.0, 1.0);
        TargetMoonProgress = (float)Math.Clamp(c.MoonProgress01, 0.0, 1.0);
        MoonPhase = (float)Math.Clamp(c.MoonPhase01, 0.0, 1.0);
        TargetCloud = (float)(c.CloudCoverPercent / 100.0);
        TargetWind = (float)Math.Clamp(c.WindKmh / 60.0, 0, 1);
        TargetRain = 0f;
        TargetSnow = 0f;
        TargetFog = 0f;
        Storm = false;

        switch (c.WmoCode)
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
        SunProgress += (TargetSunProgress - SunProgress) * k;
        MoonProgress += (TargetMoonProgress - MoonProgress) * k;

        // lightning: a cloud-lighting sheet flash is the usual event, with roughly one
        // in four striking as a drawn bolt. Both decay exponentially; the bolt also
        // fires a couple of return strokes, which is what makes real lightning flicker.
        Lightning *= (float)Math.Exp(-dt * 9.0);
        Bolt *= (float)Math.Exp(-dt * 7.5);
        if (Storm)
        {
            if (boltStrokesLeft > 0)
            {
                boltTimer -= dt;
                if (boltTimer <= 0)
                {
                    Bolt = 0.7f + (float)rng.NextDouble() * 0.3f;
                    Lightning = Math.Max(Lightning, 0.30f + (float)rng.NextDouble() * 0.25f);
                    boltStrokesLeft--;
                    boltTimer = 0.05 + rng.NextDouble() * 0.10;
                }
            }

            flashTimer -= dt;
            if (flashTimer <= 0)
            {
                if (AlwaysBolt || rng.NextDouble() < 0.25)
                {
                    // a strike: new channel, a bright flash, then 1-2 return strokes
                    BoltSeed = (float)rng.NextDouble() * 100f;
                    Bolt = 0.85f + (float)rng.NextDouble() * 0.15f;
                    Lightning = 0.45f + (float)rng.NextDouble() * 0.35f;
                    boltStrokesLeft = rng.Next(1, 3);
                    boltTimer = 0.06 + rng.NextDouble() * 0.10;
                    flashTimer = 9.0 + rng.NextDouble() * 16.0;
                }
                else
                {
                    Lightning = 0.35f + (float)rng.NextDouble() * 0.45f;
                    // occasionally a quick follow-up flash, otherwise a long gap
                    flashTimer = rng.NextDouble() < 0.30 ? 0.12 + rng.NextDouble() * 0.20
                                                         : 6.0 + rng.NextDouble() * 12.0;
                }
            }
        }
    }
}
