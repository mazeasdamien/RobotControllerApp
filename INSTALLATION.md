# RobotHub Installation Guide

## Prerequisites

- .NET 8 SDK (download from https://dot.net)
- Windows 10 / 11 x64 (or Linux x64 / arm64)
- OpenCV native runtime is bundled via `OpenCvSharp4.Windows` NuGet package — no separate install needed
- Intel RealSense SDK optional (only required if `RealSenseIntrinsics` is used)

## Development (no install)

```powershell
cd RobotHub
dotnet run
```

Dashboard: `http://localhost:5000/ui`

## Production (Windows Service)

### 1. Publish

```powershell
cd RobotHub
dotnet publish -c Release -r win-x64 --self-contained -o C:\RobotHub\publish
```

### 2. Configure

Edit `C:\RobotHub\publish\appsettings.json` with your robot IPs, relay port, and API keys.

### 3. Register the Windows Service

```powershell
sc.exe create RobotOrangeHub binPath="C:\RobotHub\publish\RobotHub.exe" start=auto DisplayName="Robot Orange Hub"
sc.exe description RobotOrangeHub "Headless relay and calibration hub for Robot Orange telepresence system"
sc.exe failure RobotOrangeHub actions=restart/5000/restart/10000// reset=60
sc.exe start RobotOrangeHub
```

### 4. Verify

```powershell
curl http://localhost:5000/status
# {"service":"RobotOrangeHub","ok":true,...}
```

Open `http://localhost:5000/ui` in a browser for the live monitoring dashboard.

## Updating an Existing Install

```powershell
sc.exe stop RobotOrangeHub
# overwrite publish folder with new build
sc.exe start RobotOrangeHub
```

## Cloudflare Tunnel

The Cloudflare tunnel configuration is unchanged. `cloudflared` still points to:
- `niryo.dmzs-lab.com` → `localhost:5000` (robot/unity WebSocket relay)
- `scene3d.dmzs-lab.com` → `localhost:8181` (3D scene WebSocket)

## Firewall

Open the same ports as before:

```powershell
netsh advfirewall firewall add rule name="RobotHub-Relay" dir=in action=allow protocol=TCP localport=5000
netsh advfirewall firewall add rule name="RobotHub-Scene3D" dir=in action=allow protocol=TCP localport=8181
netsh advfirewall firewall add rule name="RobotHub-Dashboard" dir=in action=allow protocol=TCP localport=5000
```
