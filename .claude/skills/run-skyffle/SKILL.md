---
name: run-skyffle
description: Build, launch, and visually verify the Skyffle WinUI 3 weather app on Windows (ARM64). Use when asked to run, start, build, launch, or screenshot Skyffle, or to confirm a change works in the real app.
---

# Run Skyffle

Skyffle is an unpackaged WinUI 3 (.NET 8) desktop weather app. Project file:
`Skyffle/Skyffle.csproj`. Everything below is PowerShell.

## 1. Stop any running instance first

A running Skyffle.exe locks the DLLs in `bin/` and the build (and any
folder rename/clean) fails with Access denied:

```powershell
Get-Process -Name Skyffle -ErrorAction SilentlyContinue | Stop-Process -Force
```

## 2. Build

`dotnet` is NOT on PATH in non-interactive shells on this machine — resolve
it explicitly:

```powershell
$dotnet = @("$env:ProgramFiles\dotnet\dotnet.exe", "${env:ProgramFiles(x86)}\dotnet\dotnet.exe", "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
& $dotnet build Skyffle\Skyffle.csproj -p:Platform=ARM64 -p:RuntimeIdentifier=win-arm64 --nologo -v minimal
```

Platform must be `ARM64` with RID `win-arm64` (this is an ARM64 Windows
machine). Cold build ~45s, incremental ~15s.

## 3. Launch

```powershell
Start-Process "Skyffle\bin\ARM64\Debug\net8.0-windows10.0.22621.0\win-arm64\Skyffle.exe"
Start-Sleep -Seconds 6
Get-Process -Name Skyffle | Select-Object Id, MainWindowTitle, Responding
```

Expect `MainWindowTitle = Skyffle` and `Responding = True`. On crash, check
`$env:TEMP\skyffle-crash.txt`.

Debug hooks (set before launch to preview any weather condition):
`$env:SKYFFLE_FORCE_WMO = "<WMO code>"`, optionally
`$env:SKYFFLE_FORCE_NIGHT = "1"`.

App data (saved locations, settings) lives in `%LOCALAPPDATA%\Skyffle`.

## 4. Verify visually (screenshot)

Plain `CopyFromScreen` captures the wrong region because PowerShell is DPI-
virtualized on this high-DPI machine — call `SetProcessDPIAware()` first:

```powershell
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32b {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[Win32b]::SetProcessDPIAware() | Out-Null
$h = (Get-Process -Name Skyffle).MainWindowHandle
[Win32b]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 800
$r = New-Object Win32b+RECT; [Win32b]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save("$env:TEMP\skyffle-window.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
```

Read the PNG and confirm the sky scene renders (sun/moon, forecast rows,
FEELS LIKE / HUMIDITY / WIND / PRESSURE cards). A blank or offset frame
means the DPI-aware step was skipped or the window hadn't finished loading.

If the capture shows a different window (SetForegroundWindow can silently
fail when another app holds the foreground lock), capture the window surface
directly instead of the screen — declare `PrintWindow` alongside the other
user32 imports and replace `CopyFromScreen` with:

```powershell
$hdc = $g.GetHdc()
[Win32b]::PrintWindow($h, $hdc, 2) | Out-Null  # 2 = PW_RENDERFULLCONTENT, needed for the DirectX sky
$g.ReleaseHdc($hdc)
```

This works regardless of z-order and needs no foreground activation.
