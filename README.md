# RobotHub

Headless **ASP.NET Core 8 Worker Service** replacement for the WinUI3 `RobotControllerApp`. Exposes the identical WebSocket/HTTP API surface consumed by the Unity client — zero client-side changes required.

## Why the Rewrite

| Pain Point (WinUI3) | Resolution (Worker Service) |
|---|---|
| MSIX socket binding restrictions | Plain .NET — zero friction |
| UI thread coupling on every telemetry event | Pure `async/await` via `TelemetryBus` |
| Window close kills all services | SCM `RestartOnFailure` policy |
| Windows-only deployment | Runs on Windows, Linux, Docker |
| 204 KB God-object `MainWindow.xaml.cs` | 7 focused service files + 4 thin worker shims |

## Network Surface (unchanged from WinUI3 version)

| Port | Endpoint | Protocol | Description |
|---|---|---|---|
| 5000 | `/robot?robotId=X` | WebSocket | Robot bridge clients |
| 5000 | `/unity?robotId=X` | WebSocket | Expert Unity clients |
| 5000 | `/image` | HTTP GET | Latest camera frame (JPEG) |
| 5000 | `/status` | HTTP GET | JSON health check |
| 5000 | `/status/sse` | Server-Sent Events | Live telemetry stream |
| 5000 | `/ui` | HTTP GET | Browser monitoring dashboard |
| 8181 | `/scene3d-ws` | WebSocket | 3D scene broadcast (pose + joints) |
| 8181 | `/library/{file}` | HTTP GET | GLB asset streaming |

## Installation

See [INSTALLATION.md](INSTALLATION.md) for full setup instructions.

### Quick Start (Development)

```powershell
cd RobotHub
dotnet run
```

Open the dashboard at `http://localhost:5000/ui`

### Install as Windows Service

```powershell
dotnet publish -c Release -r win-x64 --self-contained
sc.exe create RobotOrangeHub binPath="C:\path\to\RobotHub.exe" start=auto
sc.exe failure RobotOrangeHub actions=restart/5000/restart/10000// reset=60
sc.exe start RobotOrangeHub
```

## Configuration

Edit `appsettings.json`:

```json
{
  "Hub": {
    "RelayPort": 5000,
    "RobotIp": "169.254.200.200",
    "Robot2Ip": "169.254.200.201",
    "CameraRobot": 1
  }
}
```

## Architecture

```
Program.cs
├── RelayServerWorker      → RelayServerHost   (port 5000 — robot/unity WebSocket relay)
├── Scene3dWorker          → Scene3dBroadcastServer (port 8181 — Unity 3D scene feed)
├── RobotBridgeWorker      → RobotBridgeService x2  (ROS bridge to R1 and R2)
└── CameraCalibrationWorker→ CameraCalibrationService (ArUco ArUco detection loop)

TelemetryBus (Channel<TelemetryEvent>) → /status/sse → dashboard.html
```
