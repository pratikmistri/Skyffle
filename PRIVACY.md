# Skyffle Privacy Policy

**Last updated: 22 August 2026**

Skyffle is a weather app for Windows, published by Pratik Mistri ("we", "us"). This
policy explains exactly what data the app handles, where it goes, and what it does not do.

Skyffle has no accounts, no sign-in, no advertising, no analytics or telemetry SDKs, and
no third-party trackers. We do not operate a server, and we never receive, store, or see
any of your data.

## Summary

| Data | Where it goes | Kept where |
|---|---|---|
| Your device location (if you allow it) | Sent to Open-Meteo to fetch your local forecast | Held in memory only; never written to disk |
| Cities you search for and save | Search text sent to Open-Meteo; saved cities stay local | `%LOCALAPPDATA%\Skyffle\locations.json` |
| Temperature unit preference (°C/°F) | Nowhere | `%LOCALAPPDATA%\Skyffle\settings.json` |
| Crash details, if the app fails | Nowhere | `%TEMP%\skyffle-crash.txt` on your PC |

## Location

If you grant Windows location permission, Skyffle asks Windows once at each launch for
your approximate position (requested at roughly 500-metre accuracy — enough for a local
forecast, not for precise tracking). It uses that position for one purpose: to look up the
weather where you are.

**Those coordinates are sent to Open-Meteo**, the weather service Skyffle uses, as part of
the forecast and air-quality requests. This is unavoidable — a weather service cannot
return your local forecast without knowing the location to forecast for. The requests are
sent over HTTPS and carry no name, account, device identifier, or advertising ID; as with
any internet request, Open-Meteo's servers also see your IP address. Open-Meteo's handling
of that request data is governed by its own privacy policy: https://open-meteo.com/en/terms

Your device location is **never saved to disk** by Skyffle. It is held in memory for the
current session, shown in the city list as "Your location", and discarded when the app
closes; it is re-resolved from Windows the next time you open the app.

Location is entirely optional. Skyffle works fully without it — if you deny permission, or
have never granted it, the app simply shows the cities you have saved and never asks
Windows for a position. You can change this at any time in **Settings → Privacy & security
→ Location** in Windows. Revoking it takes effect the next time Skyffle asks.

## Cities you search for and save

When you type in the search box, the text you type is sent to Open-Meteo's geocoding
service so it can suggest matching places. When you pick a city, its name, country, and
coordinates are saved to a file on your PC (`%LOCALAPPDATA%\Skyffle\locations.json`) so it
is still there next time. That file never leaves your device. Removing a city from the app
removes it from that file.

## Settings

Your temperature unit preference is stored in `%LOCALAPPDATA%\Skyffle\settings.json` on
your PC and is not transmitted anywhere.

## Crash information

If Skyffle hits an unexpected error, it appends the error message and technical details to
`skyffle-crash.txt` in your Windows temporary folder so the problem can be diagnosed. This
file stays on your computer. It is never uploaded, and we never see it unless you choose to
send it to us yourself.

## Third-party services

Skyffle uses one external service: **Open-Meteo** (open-meteo.com), a free, key-less
weather API, for forecasts, place search, and air-quality data. Requests go to
`api.open-meteo.com`, `geocoding-api.open-meteo.com`, and `air-quality-api.open-meteo.com`
over HTTPS. No other network connections are made by the app.

## What Skyffle does not do

- It does not collect your name, email address, or any account information.
- It does not use advertising identifiers or show ads.
- It does not include analytics, telemetry, or crash-reporting SDKs.
- It does not track you across apps or websites.
- It does not sell or share personal information with anyone.
- It does not build a profile of you or log your location history.

## Children

Skyffle is not directed at children and does not knowingly collect personal information
from anyone, including children under 13.

## Deleting your data

All data Skyffle stores is on your own PC. Uninstalling the app, or deleting the
`%LOCALAPPDATA%\Skyffle` folder and `%TEMP%\skyffle-crash.txt`, removes it completely.
Because we hold no data about you, there is nothing for us to delete on our side.

## Changes to this policy

If this policy changes, the updated version will be published at this address and the
"Last updated" date above will change.

## Contact

Questions about this policy: pratikmistri@gmail.com
