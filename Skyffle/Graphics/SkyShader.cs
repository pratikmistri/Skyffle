using ComputeSharp;
using ComputeSharp.D2D1;

namespace Skyffle.Graphics;

/// <summary>
/// Full-screen atmospheric shader. All weather conditions are expressed as
/// continuous parameters so the CPU side can cross-fade between states.
/// </summary>
[D2DInputCount(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct SkyShader : ID2D1PixelShader
{
    private readonly float time;
    private readonly float2 resolution;
    private readonly float daylight;   // 0 = deep night, 1 = full day
    private readonly float cloud;      // cloud coverage 0..1
    private readonly float rain;       // rain intensity 0..1
    private readonly float snow;       // snow intensity 0..1
    private readonly float fog;        // fog density 0..1
    private readonly float lightning;  // instantaneous flash brightness 0..1
    private readonly float wind;       // -1..1, slants precipitation
    private readonly float pxScale;    // render pixels per effective pixel; keeps precipitation the same size at any window size
    private readonly float4 cardA;     // UI surfaces rain can land on: x, y, w, h in scene pixels
    private readonly float4 cardB;
    private readonly float4 cardC;
    private readonly float4 cardD;
    private readonly float4 detailsGrid; // uniform grid of small cards: x, y, cellW, cellH
    private readonly float4 detailsMeta; // columns, item count, cell inner margin, unused

    public SkyShader(float time, float2 resolution, float daylight, float cloud,
                     float rain, float snow, float fog, float lightning, float wind,
                     float pxScale,
                     float4 cardA, float4 cardB, float4 cardC, float4 cardD,
                     float4 detailsGrid, float4 detailsMeta)
    {
        this.time = time;
        this.resolution = resolution;
        this.daylight = daylight;
        this.cloud = cloud;
        this.rain = rain;
        this.snow = snow;
        this.fog = fog;
        this.lightning = lightning;
        this.wind = wind;
        this.pxScale = pxScale;
        this.cardA = cardA;
        this.cardB = cardB;
        this.cardC = cardC;
        this.cardD = cardD;
        this.detailsGrid = detailsGrid;
        this.detailsMeta = detailsMeta;
    }

    /// <summary>
    /// Rect of the detail-grid card in the cell under pos (shifted down by rowOffset rows).
    /// Cells past the item count, or outside the grid, come back zero-sized.
    /// </summary>
    private static float4 DetailCell(float2 pos, float4 grid, float4 meta, float rowOffset)
    {
        float2 local = pos - new float2(grid.X, grid.Y);
        float col = Hlsl.Floor(local.X / grid.Z);
        float row = Hlsl.Floor(local.Y / grid.W) + rowOffset;
        float idx = row * meta.X + col;
        float valid = Hlsl.Step(0.0f, col) * Hlsl.Step(col, meta.X - 1.0f)
                    * Hlsl.Step(0.0f, row) * Hlsl.Step(0.0f, idx)
                    * Hlsl.Step(idx, meta.Y - 1.0f) * Hlsl.Step(1.0f, meta.Y);
        float m = meta.Z;
        return new float4(
            grid.X + col * grid.Z + m,
            grid.Y + row * grid.W + m,
            (grid.Z - 2.0f * m) * valid,
            (grid.W - 2.0f * m) * valid);
    }

    /// <summary>1 when p lies inside the card rect (cards with no height never match).</summary>
    private static float InsideCard(float2 p, float4 card)
    {
        return Hlsl.Step(card.X, p.X) * Hlsl.Step(p.X, card.X + card.Z)
             * Hlsl.Step(card.Y, p.Y) * Hlsl.Step(p.Y, card.Y + card.W)
             * Hlsl.Step(1.0f, card.W);
    }

    /// <summary>Splash droplets arcing off one card's top edge; returns brightness at pos.</summary>
    private static float SplashForCard(float2 pos, float4 card, float t, float rain)
    {
        // inset a little so drops don't spray off the rounded corners
        float inSpan = Hlsl.Step(card.X + 6.0f, pos.X) * Hlsl.Step(pos.X, card.X + card.Z - 6.0f)
                     * Hlsl.Step(1.0f, card.W);
        float edgeDist = card.Y - pos.Y;
        if (inSpan * Hlsl.Step(-2.0f, edgeDist) * Hlsl.Step(edgeDist, 30.0f) < 0.5f)
        {
            return 0.0f;
        }

        float splash = 0.0f;
        for (int c = -1; c <= 1; c++)
        {
            float cell = Hlsl.Floor(pos.X / 26.0f) + (float)c;
            float hc = Hash11(cell * 0.731f + 7.7f);
            float cx = (cell + 0.5f) * 26.0f;
            float period = 0.45f + hc * 0.55f;
            float ph = Hlsl.Frac(t / period + hc * 11.3f);
            float act = Hlsl.Step(1.0f - rain * 0.9f, Hlsl.Frac(hc * 3.9f));
            for (int s = 0; s < 2; s++)
            {
                float dir = (float)s * 2.0f - 1.0f;
                float h2 = Hlsl.Frac(hc * (13.3f + (float)s * 5.1f));
                // little parabolic arc: out along the edge, up, then pulled back down
                float dx = dir * (5.0f + 14.0f * h2) * ph;
                float dy = -(16.0f + 10.0f * h2) * ph + 26.0f * ph * ph;
                float2 dp = new(cx + dx, card.Y + dy);
                splash += Hlsl.SmoothStep(2.4f, 0.7f, Hlsl.Length(pos - dp)) * (1.0f - ph) * act;
            }
        }
        return splash;
    }

    private static float Hash11(float p)
    {
        p = Hlsl.Frac(p * 0.1031f);
        p *= p + 33.33f;
        p *= p + p;
        return Hlsl.Frac(p);
    }

    private static float Hash21(float2 p)
    {
        float3 p3 = Hlsl.Frac(new float3(p.X, p.Y, p.X) * 0.1031f);
        p3 += Hlsl.Dot(p3, new float3(p3.Y, p3.Z, p3.X) + 33.33f);
        return Hlsl.Frac((p3.X + p3.Y) * p3.Z);
    }

    private static float Noise(float2 p)
    {
        float2 i = Hlsl.Floor(p);
        float2 f = Hlsl.Frac(p);
        float2 u = f * f * (3.0f - 2.0f * f);
        float a = Hash21(i);
        float b = Hash21(i + new float2(1.0f, 0.0f));
        float c = Hash21(i + new float2(0.0f, 1.0f));
        float d = Hash21(i + new float2(1.0f, 1.0f));
        return Hlsl.Lerp(Hlsl.Lerp(a, b, u.X), Hlsl.Lerp(c, d, u.X), u.Y);
    }

    private static float Fbm(float2 p)
    {
        float v = 0.0f;
        float amp = 0.5f;
        for (int i = 0; i < 5; i++)
        {
            v += amp * Noise(p);
            p = p * 2.03f + new float2(17.1f, 9.2f);
            amp *= 0.5f;
        }
        return v;
    }

    public float4 Execute()
    {
        float2 pos = D2D.GetScenePosition().XY;
        float2 uv = pos / this.resolution;                       // 0..1, top-left origin
        float2 ar = new(this.resolution.X / this.resolution.Y, 1.0f);
        float2 p = uv * ar;                                      // aspect-corrected
        float2 pp = pos / this.pxScale;                          // effective pixels: constant physical size at any window size
        float t = this.time;

        // ----- base sky gradient -----
        float3 dayTop = Hlsl.Lerp(new float3(0.13f, 0.38f, 0.78f), new float3(0.33f, 0.38f, 0.46f), this.cloud);
        float3 dayBot = Hlsl.Lerp(new float3(0.55f, 0.74f, 0.94f), new float3(0.56f, 0.59f, 0.64f), this.cloud);
        float3 nightTop = Hlsl.Lerp(new float3(0.010f, 0.020f, 0.060f), new float3(0.030f, 0.035f, 0.055f), this.cloud);
        float3 nightBot = Hlsl.Lerp(new float3(0.060f, 0.100f, 0.200f), new float3(0.070f, 0.080f, 0.110f), this.cloud);

        // golden-hour warmth when daylight is between night and day
        float dusk = Hlsl.SmoothStep(0.0f, 0.5f, this.daylight) * (1.0f - Hlsl.SmoothStep(0.5f, 1.0f, this.daylight));
        float3 top = Hlsl.Lerp(nightTop, dayTop, this.daylight);
        float3 bot = Hlsl.Lerp(nightBot, dayBot, this.daylight);
        bot = Hlsl.Lerp(bot, new float3(0.85f, 0.48f, 0.30f), dusk * (1.0f - this.cloud) * 0.55f);

        float3 col = Hlsl.Lerp(top, bot, Hlsl.Pow(Hlsl.Max(uv.Y, 0.0f), 1.25f));

        // ----- sun -----
        float2 sunPos = new(ar.X * 0.72f, 0.20f);
        float sunD = Hlsl.Length(p - sunPos);
        float clearSky = 1.0f - this.cloud;
        float sunCore = Hlsl.SmoothStep(0.055f, 0.035f, sunD);
        float sunGlow = Hlsl.Exp(-sunD * 5.5f) * 0.45f + Hlsl.Exp(-sunD * 14.0f) * 0.40f;
        float3 sunCol = Hlsl.Lerp(new float3(1.0f, 0.55f, 0.25f), new float3(1.0f, 0.92f, 0.72f), Hlsl.SmoothStep(0.3f, 0.8f, this.daylight));
        col += sunCol * (sunCore * 1.2f + sunGlow) * this.daylight * (0.04f + 0.96f * clearSky);

        // ----- moon + stars -----
        float night = 1.0f - this.daylight;
        if (night > 0.01f)
        {
            float2 moonPos = new(ar.X * 0.30f, 0.16f);
            float moonD = Hlsl.Length(p - moonPos);
            float moon = Hlsl.SmoothStep(0.045f, 0.040f, moonD);
            float crater = Hlsl.SmoothStep(0.045f, 0.040f, Hlsl.Length(p - moonPos - new float2(0.016f, -0.006f)));
            float moonGlow = Hlsl.Exp(-moonD * 9.0f) * 0.35f;
            col += (new float3(0.92f, 0.94f, 1.00f) * Hlsl.Saturate(moon - crater * 0.85f) + new float3(0.55f, 0.62f, 0.85f) * moonGlow)
                   * night * clearSky;

            // star field: sparse hash sprinkle, twinkling
            float2 cell = Hlsl.Floor(pos / 3.0f);
            float h = Hash21(cell);
            float star = Hlsl.SmoothStep(0.997f, 1.0f, h);
            float twinkle = 0.55f + 0.45f * Hlsl.Sin(t * 2.2f + h * 251.0f);
            col += new float3(0.9f, 0.93f, 1.0f) * star * twinkle * night * clearSky * (1.0f - uv.Y) * 0.9f;
        }

        // ----- clouds (two drifting fbm layers) -----
        if (this.cloud > 0.01f)
        {
            float cover = this.cloud;
            float f1 = Fbm(p * 2.6f + new float2(t * 0.020f, 0.0f));
            float f2 = Fbm(p * 4.9f + new float2(t * 0.045f, 3.7f));
            float shape = f1 * 0.65f + f2 * 0.35f;
            float dens = Hlsl.SmoothStep(0.95f - cover * 0.75f, 1.05f - cover * 0.45f, shape);
            dens *= Hlsl.SmoothStep(0.0f, 0.35f, uv.Y) * 0.9f + 0.1f;   // thinner at zenith
            float3 cloudDay = Hlsl.Lerp(new float3(0.98f, 0.98f, 1.00f), new float3(0.62f, 0.64f, 0.70f), cover * 0.8f);
            float3 cloudNight = new(0.10f, 0.11f, 0.15f);
            float3 cloudCol = Hlsl.Lerp(cloudNight, cloudDay, this.daylight);
            // storm clouds darken further with rain
            cloudCol = Hlsl.Lerp(cloudCol, cloudCol * 0.55f, Hlsl.Saturate(this.rain * 1.2f));
            col = Hlsl.Lerp(col, cloudCol, Hlsl.Saturate(dens));
        }

        // ----- rain (three parallax layers of streaks) -----
        if (this.rain > 0.01f)
        {
            // rain lands on the glass cards: no streaks on a card's face,
            // and drops scatter off its top edge
            float4 cellHere = DetailCell(pos, this.detailsGrid, this.detailsMeta, 0.0f);
            float4 cellBelow = DetailCell(pos, this.detailsGrid, this.detailsMeta, 1.0f);
            float onGlass = Hlsl.Saturate(
                InsideCard(pos, this.cardA) + InsideCard(pos, this.cardB) +
                InsideCard(pos, this.cardC) + InsideCard(pos, this.cardD) +
                InsideCard(pos, cellHere));
            float aboveSurface = 1.0f - onGlass;

            float slant = this.wind * 0.35f;
            for (int i = 0; i < 3; i++)
            {
                float li = (float)i;
                float scale = 1.0f + li * 0.9f;
                // 18 epx columns, 320 epx streak cycles: drop size no longer grows with the window
                float2 q = new((pp.X + pp.Y * slant) / (18.0f * scale), pp.Y / (320.0f * scale));
                float colId = Hlsl.Floor(q.X);
                float h = Hash11(colId + li * 57.31f);
                float speed = 2.6f + h * 1.8f - li * 0.4f;
                float yy = Hlsl.Frac(q.Y - t * speed + h * 19.7f);
                float trail = Hlsl.Pow(Hlsl.Max(yy, 0.0f), 9.0f); // bright head leads the fall
                float xf = Hlsl.Abs(Hlsl.Frac(q.X) - 0.5f);
                float width = Hlsl.SmoothStep(0.11f, 0.02f, xf);
                float active = Hlsl.Step(1.0f - this.rain * 0.85f, h);   // more columns as intensity rises
                float layerFade = 1.0f - li * 0.28f;
                col += new float3(0.62f, 0.72f, 0.88f) * trail * width * active * layerFade * 0.55f
                       * (0.35f + 0.65f * this.daylight) * aboveSurface;
            }

            // ----- splashes: droplets scattering off each card's top edge -----
            float splash = SplashForCard(pos, this.cardA, t, this.rain)
                         + SplashForCard(pos, this.cardB, t, this.rain)
                         + SplashForCard(pos, this.cardC, t, this.rain)
                         + SplashForCard(pos, this.cardD, t, this.rain)
                         + SplashForCard(pos, cellHere, t, this.rain)
                         + SplashForCard(pos, cellBelow, t, this.rain);
            col += new float3(0.75f, 0.83f, 0.95f) * splash * this.rain * (0.35f + 0.65f * this.daylight);
        }

        // ----- snow (three parallax layers of drifting flakes) -----
        if (this.snow > 0.01f)
        {
            for (int i = 0; i < 3; i++)
            {
                float li = (float)i;
                float cellPx = 130.0f - li * 40.0f; // epx per flake cell, nearer layers larger
                float fall = 170.0f + li * 80.0f;   // epx per second
                float2 q = pp / cellPx;
                q.Y -= t * fall / cellPx; // subtract so flakes translate downward
                q.X += Hlsl.Sin(t * 0.7f + li * 2.1f + pp.Y * 0.0025f) * 0.8f + this.wind * t * 1.5f;
                float2 cell = Hlsl.Floor(q);
                float2 f = Hlsl.Frac(q);
                float h = Hash21(cell + li * 31.7f);
                float2 jitter = new(Hlsl.Frac(h * 13.7f), Hlsl.Frac(h * 71.3f));
                float d = Hlsl.Length(f - (0.25f + jitter * 0.5f));
                float radius = 0.06f + Hlsl.Frac(h * 5.1f) * 0.07f;
                float flake = Hlsl.SmoothStep(radius, radius * 0.35f, d);
                float active = Hlsl.Step(1.0f - this.snow * 0.75f, Hlsl.Frac(h * 3.3f));
                float layerFade = 1.0f - li * 0.25f;
                col += new float3(0.95f, 0.96f, 1.0f) * flake * active * layerFade * 0.8f
                       * (0.45f + 0.55f * this.daylight);
            }
        }

        // ----- fog -----
        if (this.fog > 0.01f)
        {
            float fogNoise = Fbm(p * 3.0f + new float2(t * 0.03f, t * 0.008f));
            float fogAmt = this.fog * (0.45f + 0.55f * fogNoise) * (0.35f + 0.65f * uv.Y);
            float3 fogCol = Hlsl.Lerp(new float3(0.16f, 0.17f, 0.21f), new float3(0.78f, 0.80f, 0.84f), this.daylight);
            col = Hlsl.Lerp(col, fogCol, Hlsl.Saturate(fogAmt));
        }

        // ----- lightning flash -----
        col += new float3(0.90f, 0.92f, 1.0f) * this.lightning;

        // ----- gentle vignette for depth -----
        float2 vc = uv - 0.5f;
        col *= 1.0f - Hlsl.Dot(vc, vc) * 0.45f;

        return new float4(Hlsl.Saturate(col), 1.0f);
    }
}
