# Changelog

All notable changes are documented in this file.

---

## [2.0.0] — 2026-04-02

### Added — RobotHub Worker Service
- Created new `RobotHub/` project as a headless **ASP.NET Core 8 Worker Service** replacing the WinUI3 `RobotControllerApp` hub.
- `TelemetryBus.cs` — bounded `Channel<TelemetryEvent>` replacing all `DispatcherQueue.TryEnqueue` callbacks.
- `Workers/RelayServerWorker.cs` — `IHostedService` shim for `RelayServerHost`.
- `Workers/Scene3dWorker.cs` — `IHostedService` shim for `Scene3dBroadcastServer`.
- `Workers/RobotBridgeWorker.cs` — `IHostedService` shim for `RobotBridgeService` (both R1 and R2).
- `Workers/CameraCalibrationWorker.cs` — `IHostedService` shim for `CameraCalibrationService`.
- `wwwroot/dashboard.html` — glassmorphism browser dashboard consuming `/status/sse` via `EventSource`.
- `Program.cs` — host builder wiring DI, SCM Windows Service support, SSE `/status/sse` endpoint, and static dashboard on port 5001.
- `appsettings.json` — structured configuration replacing JSON filesystem config in `AppSettings.cs`.
- `README.md` and `INSTALLATION.md` for the new project.

### Changed — Scene3dBroadcastServer (Hub copy)
- Added `static Instance` singleton property for worker access without instance injection.
- Added `static BroadcastPayload()` convenience wrapper.
- Removed `AssetsPath` hard-guard — server now starts without a local assets directory configured.
- Static file middleware is skipped when `AssetsPath` is not set.

### Architecture — unchanged external API
- Unity client (Robot_Orange) requires **zero changes** — same WebSocket URLs on ports 5000 and 8181.
- Cloudflare tunnel configuration unchanged.

---

## [1.x.x] — prior to 2026-04-02

See `RobotControllerApp/` git history.
