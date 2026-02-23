# Robot Orange - Controller Dashboard (C# WinUI 3)

### Core Architecture
This application serves as the primary ground-station bridge and telemetry visualizer for the Robot Orange XR Telepresence environment. It integrates:
- **RelayServerHost**: A local Kestrel-based WebSocket server bridging Quest 3 remote expert clients with physical `rosbridge_server` instances.
- **RobotBridgeService**: Bi-directional asynchronous TCP WebSocket bridges managing continuous joint telemetry and command flow to multiple ROS instances.
- **ConnectionManager**: A thread-safe concurrent registry handling dynamic connections and real-time state mutations.
- **WinUI 3 Dashboard**: An optimized, low-latency telemetry panel showing active sub-systems, connection graphs, visual metrics, and operator endpoints.

### Setup
1. **Restore dependencies**: Normal `dotnet restore` flow.
2. **Build**: Build via VS or CLI (`dotnet build`). Code-generated `x:Name` references and visual states in XAML will seamlessly compile into `.g.cs` files prior to code link.
3. **Execution**: Requires Windows App SDK runtime natively. 

### Recent Refactoring
- **Deprecation Cleanups**: Removed bloated dead references, experimental test files (e.g. `TestApi.cs`), and outdated components.
- **Memory/Range Syntax**: Deployed modern C# 8 `System.Index` structural slice allocations (`[..]`) cutting standard string operation overhead. `using`-declarations handle scoped allocations natively to prevent garbage-collector stutters.
- **Logging Topology**: Removed non-critical, decorative string-interpolated polling logs to prevent internal `Console` and background UI-log buffer lock contention, elevating strictly `Warning`/`Error` and fundamental state shifts.
- **Visuals Fix**: Connected structural naming identifiers to animated XAML graphics to ensure conditional `Visibility` binding resolves perfectly when connection conditions fluctuate (i.e. preventing 'phantom active pulses').
