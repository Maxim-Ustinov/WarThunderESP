# WarThunderESP 

WarThunderESP is an educational offline research overlay prototype for War Thunder, written in C#/.NET. It reads data from the local aces.exe process and draws a transparent overlay with projected unit boxes and labels.

This project is intended for offline/no-anti-cheat research, code-architecture study, and game-memory analysis experiments.

## No Anti-Cheat Bypass

WarThunderESP does not implement any anti-cheat bypass, stealth mechanism, driver, injection, hook, memory patching, or protection-disabling technique.

It uses standard Windows API functions such as OpenProcess, ReadProcessMemory, VirtualQueryEx, GetMappedFileNameW, GetSystemMetrics, and GetAsyncKeyState.

## Caution

Do not use this with anti-cheat enabled, in live online matches, or on an account you care about. Because this project only uses normal user-mode process memory reads and does not bypass anti-cheat, running it against a protected game session will probably get you banned.

When anti-cheat is disabled in the War Thunder launcher, the official client may limit which game modes are available, for example to Arcade Battles only depending on the current client rules. This project assumes that restricted no-anti-cheat environment and does not attempt to bypass those limits.

## Features

- Reads War Thunder process memory through `kernel32.dll` / `psapi.dll`.
- Scans readable process memory for known ground and aircraft vtables.
- Projects world positions to screen space using view/projection or GLOBTM matrices.
- Draws ESP boxes and labels through `GameOverlay.Net`.
- Supports `F10` overlay toggle.

## Requirements

- Windows x64.
- .NET SDK 10.0 or newer.
- War Thunder `aces.exe` running in an offline/no-anti-cheat environment.
- NuGet package: `GameOverlay.Net` 4.3.1.

## Build

```powershell
dotnet restore
dotnet build WarThunderESP.sln
```

## Run

```powershell
dotnet run --project WarThunderESP\WarThunderESP.csproj
```

Start the game first, then run WarThunderESP. The console prints the resolved module base and vtable addresses. Press `F10` to enable/disable ESP and press `Enter` in the console to exit.

## Project Layout

```text
WarThunderESP/
  Domain/             Shared data types used by memory and overlay code.
  Memory/             Process access, scanning, coordinate reading, projection.
    GameMemoryReader.cs
    GameMemoryReader.Entities.cs
    GameMemoryReader.Projection.cs
    GameMemoryReader.Scanning.cs
  Overlay/            Transparent overlay window and drawing code.
  GlobalUsings.cs     Shared imports for the project.
  Program.cs          Application entry point.
  WarThunderESP.csproj Project file and NuGet dependencies.
NuGet.Config          Project-local NuGet source configuration.
```

## Notes

Offsets and vtable addresses are game-version-specific. If War Thunder updates, the ESP may stop working until the offsets are refreshed.

## Disclaimer

This repository is provided for educational and research purposes only. The author is not responsible for misuse of this code or violations of third-party terms of service.
