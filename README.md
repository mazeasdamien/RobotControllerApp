# 🤖 Robot Orange — Controller Dashboard

> **WinUI 3 ground-station bridge** for the Niryo robot XR Telepresence system.  
> Connects a Meta Quest headset operator to one or two physical Niryo Ned robots over ROS, via a Cloudflare-tunnelled relay.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                   WinUI 3 Dashboard                     │
│   Camera feed · Joint telemetry · 3D scene preview      │
└────────────────────┬────────────────────────────────────┘
                     │  Kestrel
        ┌────────────┴─────────────┐
        │      RelayServerHost     │  WebSocket hub (port 8181)
        │   /scene3d-ws  /transcribe│  Cloudflare tunnel → scene3d.dmzs-lab.com
        └──┬──────────────────┬────┘
           │                  │
  ┌────────┴──────┐   ┌───────┴────────────────┐
  │RobotBridge    │   │ Scene3dBroadcast        │
  │Service (×2)   │   │ Server                 │
  │  TCP → ROS    │   │  GLB delivery · camera  │
  │  joint states │   │  pose · scan results    │
  └───────────────┘   └────────────────────────┘
           │
  ┌────────┴──────────┐
  │  rosbridge_server │  (ROS 1 on robot)
  │  Niryo Ned ×2     │
  └───────────────────┘
```

---

## Services

| Service | File | Role |
|---|---|---|
| **RelayServerHost** | `Services/RelayServerHost.cs` | Kestrel WebSocket hub. Bridges Quest 3 remote expert clients ↔ physical `rosbridge_server`. Exposes `/scene3d-ws` and `/transcribe`. |
| **RobotBridgeService** | `Services/RobotBridgeService.cs` | Bi-directional async TCP / WebSocket bridge. Streams joint telemetry and commands to multiple ROS instances at ≤10 Hz. |
| **Scene3dBroadcastServer** | `Services/Scene3dBroadcastServer.cs` | Handles GLB model delivery (via WebSocket binary frames), camera pose, scan results, and orient images to every connected `preview.html` client. |
| **CameraCalibrationService** | `Services/CameraCalibrationService.cs` | Computes and stores camera extrinsics. Broadcasts pose updates to the 3D preview. |
| **ConnectionManager** | `Services/ConnectionManager.cs` | Thread-safe concurrent registry. Tracks active robot bridges and XR sessions; emits state-change events to the WinUI dashboard. |
| **AppSettings** | `Services/AppSettings.cs` | Persists user config (robot IPs, port, Cloudflare URL) across sessions. |

---

## 3D Scene Preview (`Assets/preview.html`)

A self-contained **Three.js WebXR** application served via the relay to any browser or Meta Quest headset.

### Features
- **Two Niryo Ned 3D models** with per-joint FK driven from live `joint_states` ROS topic
- **IK solver** — damped-least-squares Jacobian, drives ghost robot + real arm via WS
- **WebXR Mixed Reality (AR/VR)** — passthrough on Quest 3, grabbable HTMLMesh UI panels, bimanual world navigation
- **Cloudflare tunnel** — permanent HTTPS URL `https://scene3d.dmzs-lab.com` for remote access
- **Voice transcription** — push-to-talk → Whisper API → robot command pipeline
- **Scene scanning** — photos + AI object detection → optional Tripo3D GLB generation → placed in 3D scene
- **Live camera feed** — projected as a 3D frustum overlay calibrated to the robot camera pose
- **OrbitControls** — left-drag orbit · right-drag pan · scroll zoom, centred on the table

### Performance Panel
Click **Perf** in the top bar to open the custom performance overlay:

| Metric | Description |
|---|---|
| **FPS** | Frames per second · colour-coded (blue ≥50 · yellow ≥30 · red <30) |
| **MS** | GPU submit time per frame (ms) |
| **MB** | JS heap usage (Chrome only) |
| **Bar graph** | 20-second FPS history |
| **Tunnel Requests** | Total WS + HTTP messages through the Cloudflare tunnel since page load |
| **Req/s** | Live request rate with animated bar |

### Key Performance Optimisations Applied
- `MeshStandardMaterial` only — no `MeshPhysicalMaterial` clearcoat (eliminated `getProgramCacheKey` bottleneck)
- Shadow maps disabled globally (`renderer.shadowMap.enabled = false`)
- Pre-allocated scratch `Vector3` / `Quaternion` — zero GC in the render loop
- HTMLMesh refresh driven by `MutationObserver` (dirty flag) — zero GPU upload when UI is static
- Label sprite cache — `Canvas/Texture` reused across `renderObjects()` calls
- IK solve skipped in XR mode; throttled to 1mm movement threshold in 2D mode

---

### Cloudflare Tunnel
The relay is exposed via a permanent Cloudflare tunnel:
```
https://scene3d.dmzs-lab.com  →  http://localhost:8181
```
Configure with `cloudflared tunnel run` or as a Windows service.

---

## Project Structure

```
RobotControllerApp/
├── Assets/
│   ├── preview.html          # Three.js WebXR 3D scene (self-contained)
│   └── ned.glb               # Niryo Ned 3D model
├── Services/
│   ├── RelayServerHost.cs
│   ├── RobotBridgeService.cs
│   ├── Scene3dBroadcastServer.cs
│   ├── CameraCalibrationService.cs
│   ├── ConnectionManager.cs
│   └── AppSettings.cs
├── MainWindow.xaml(.cs)       # Main dashboard UI + service orchestration
├── App.xaml(.cs)
└── RobotControllerApp.csproj
```

---

## Changelog (recent sessions)

| Date | Change |
|---|---|
| 2026-03-13 | Added custom Perf panel (FPS/MS/MB + Cloudflare tunnel request counter) |
| 2026-03-13 | Applied Three.js performance fixes: removed `MeshPhysicalMaterial`, shadow cleanup, GC-free render loop, sprite cache |
| 2026-03-13 | Moved control buttons to top bar; VR action bar in headset overlay |
| 2026-03-12 | OrbitControls centred on table; left-drag orbit, right-drag pan |
| 2026-03-12 | GLB delivery via WebSocket binary frames (bypasses Cloudflare HTTP limits) |
| 2026-03-12 | IK removed from VR mode; FK-only robot control via voice→script pipeline |
| 2026-03-11 | HTMLMesh VR panels: grabbable, dirty-flag MutationObserver refresh |
| 2026-03-11 | Bimanual hand-tracking world navigation (rotate + translate + vertical lift) |
