# PeekHUD

> **Peek at your stats — lightweight & instant telemetry.**

A lightweight, standalone hardware overlay HUD for PC gamers and power users built on .NET 8 WPF. Provides low-overhead telemetry without heavy background suites or forced accounts.

---

> [!NOTE]
> **Windows SmartScreen Notice:** Because PeekHUD is an independent open-source project and does not use an enterprise code-signing certificate, Windows Defender SmartScreen may display a *"Windows protected your PC"* prompt. Click **More info** → **Run anyway** to launch the installer.

---

## Features

- **Low-Overhead Telemetry:** Direct sensor polling via `LibreHardwareMonitorLib` and framerate tracking via ETW `TraceEvent`.
- **Core Metrics:** FPS, CPU Temp/Load, GPU Temp/Load, VRAM usage, and System RAM.
- **Customizable HUD:** Switch between numeric readouts and micro-graphs, customize HUD positioning, scaling, and background opacity.
- **Themes & Color Styling:** Ships with a high-contrast dark mode and electric cyan accents by default, plus Dark/Light theme switching and custom color pickers for every metric.
- **Dynamic Warning Alerts:** Metrics automatically shift to amber or red when temperatures, loads, or FPS hit custom thresholds.
- **Global Hotkey Toggle:** Instantly hide or show the HUD in-game using a customizable shortcut (default `Ctrl+Shift+F`).
- **Task Scheduler Startup:** Run automatically at Windows boot minimized to system tray with zero UAC prompts.

---

## Installation & Downloads

Grab the latest release from the [**Releases Page**](https://github.com/maximd7/PeekHUD/releases/latest):

- **Installer (`PeekHUD-Setup-v1.0.exe`):** Recommended for most users. Installs to Program Files and adds an optional desktop shortcut.
- **Portable (`PeekHUD-v1.0-win-x64.zip`):** Standalone archive. Extract anywhere and launch `PeekHUD.exe` directly.

---

## Tech Stack

- **Framework:** C# / .NET 8 (WPF)
- **Monitoring Engines:** LibreHardwareMonitorLib, Microsoft.Diagnostics.Tracing.TraceEvent
- **Installer:** Inno Setup 7
