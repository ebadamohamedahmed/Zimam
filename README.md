# Zimam (زمام) — Broadcast Control & Monitoring Engine

A lightweight, portable, plugin-based broadcast control system built with C# (.NET 10), ASP.NET Core SignalR, and WebView2.

## Key Features
- **Plugin Architecture (`Zimam.Abstractions`):** Drop compiled device `.dll` drivers into `/Plugins` with zero changes to the host.
- **3-Pillar Device Design:** Standardized **Control**, **Monitor** (with alarm levels), and **Management** tabs for every device.
- **Bilingual UI (AR / EN):** Instant RTL/LTR language switching and offline-bundled SignalR assets.
- **OB Van Safety:** Starts in **Safe Mode (Monitor-Only)** by default and records every command to `tx_audit.log`.

## Included Drivers
- **HyperDeck (`Zimam.Driver.HyperDeck`):** TCP `9993` transport control, live timecode/slot telemetry, and FTP clip list/rename/download/upload with progress bar.
- **FOR-A FA-505 (`Zimam.Driver.ForaFA505`):** TCP `50011` 5-channel video/color shading and preset recall.
- **Evertz Quartz Router (`Zimam.Driver.EvertzQuartz`):** TCP `4000` XY routing panel, level selection (V/A/VA), destination locking, and Salvo firing.
