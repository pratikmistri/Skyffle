# Skyffle

A native **WinUI 3** weather app for Windows, styled after Apple Weather, with a fully
GPU-shader-rendered animated sky that reacts to live conditions.

![platform](https://img.shields.io/badge/platform-Windows%2011-blue) ![framework](https://img.shields.io/badge/WinUI-3-purple)

## Features

- **Live weather** from [Open-Meteo](https://open-meteo.com/) — free, open, no API key
- **Animated shader sky**: a single parameterized HLSL pixel shader (authored in C# via
  [ComputeSharp](https://github.com/Sergio0694/ComputeSharp), rendered with Win2D) draws
  day/night gradients, golden hour, sun, moon, twinkling stars, drifting fbm clouds,
  3-layer parallax rain and snow, fog, and lightning — and **cross-fades** smoothly when
  conditions or cities change
- **Apple Weather-style UI**: hero temperature, 24-hour scroller, 10-day forecast with
  temperature-range bars, and detail cards (feels like, humidity + dew point, wind + gusts,
  pressure, UV index, visibility, sunrise/sunset, precipitation, US AQI)
- **Multi-city**: search any place worldwide (Open-Meteo geocoding), saved to
  `%LOCALAPPDATA%\Skyffle\locations.json`

## Building

Requires:
- .NET 9 SDK (builds the `net8.0-windows` target; ComputeSharp's source generators need its Roslyn)
- VS 2022 **Build Tools** with *UWP build tools* component (for PRI generation), or full Visual Studio

```powershell
dotnet build Skyffle/Skyffle.csproj -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64
```

(Use `x64`/`win-x64` on Intel/AMD machines.)

The app is unpackaged (`WindowsPackageType=None`) and self-contained w.r.t. the Windows App SDK —
no MSIX or runtime installer needed to run the build output.

## Architecture

| Piece | Where |
|---|---|
| Shader (sky, all conditions) | `Graphics/SkyShader.cs` |
| Condition → shader params + easing, lightning driver | `Graphics/SkyState.cs` |
| Open-Meteo client (forecast, geocoding, AQI) | `Services/OpenMeteoClient.cs` |
| View model (hourly/daily/details mapping) | `ViewModels/MainViewModel.cs` |
| UI | `MainWindow.xaml` |
