using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotHub.Services;

namespace RobotHub.Workers
{
    // Wraps RobotBridgeService (x2 for R1 and R2) as IHostedService background workers.
    // Publishes telemetry snapshots to TelemetryBus every 500ms.
    public sealed class RobotBridgeWorker : BackgroundService
    {
        private readonly AppSettings _settings;
        private readonly ILogger<RobotBridgeWorker> _logger;

        // Shared state snapshot — written by event callbacks on the thread pool,
        // read by the publish loop. Fields are simple value types so no lock needed.
        private volatile bool _r1Connected;
        private volatile bool _r2Connected;
        private string _r1Status = "DISCONNECTED";
        private string _r2Status = "DISCONNECTED";
        private int _r1RpiTemp;
        private int _r2RpiTemp;
        private float[] _r1Joints = new float[6];
        private float[] _r2Joints = new float[6];
        private int _cameraFps;

        private RobotBridgeService? _bridge1;
        private RobotBridgeService? _bridge2;

        // Keep the log handler reference so we can unsubscribe on stop
        private readonly Action<string> _logHandler;

        public RobotBridgeWorker(AppSettings settings, ILogger<RobotBridgeWorker> logger)
        {
            _settings = settings;
            _logger = logger;

            // Single static log subscription shared by both bridges registered once here,
            // not inside BuildBridge (which would subscribe it twice).
            _logHandler = msg => _logger.LogDebug("{Message}", msg);
            RobotBridgeService.OnLog += _logHandler;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _bridge1 = BuildBridge("Robot_Niryo_01", _settings.RobotIp, hasCamera: _settings.CameraRobot == 1);
            _bridge2 = BuildBridge("Robot_Niryo_02", _settings.Robot2Ip, hasCamera: _settings.CameraRobot == 2);

            // Wire joint updates from the RelayServerHost intercept point —
            // this is where joint data actually lands after passing through the relay.
            RobotRelayService.OnJointsReceived += joints => _r1Joints = joints;
            RobotRelayService.OnRobot2JointsReceived += joints => _r2Joints = joints;
            RobotRelayService.OnImageStatsUpdated += (fps, total) => _cameraFps = fps;

            _bridge1.Start();
            _bridge2.Start();

            _logger.LogInformation("[RobotBridgeWorker] Both robot bridge loops started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                PublishSnapshot();
                await Task.Delay(500, stoppingToken);
            }
        }

        private void PublishSnapshot()
        {
            TelemetryBus.Writer.TryWrite(new TelemetryEvent(
                _r1Connected,
                _r2Connected,
                RobotRelayService.UnityClientConnected,
                (float[])_r1Joints.Clone(),
                (float[])_r2Joints.Clone(),
                RobotRelayService.LastUnityLatencyMs,
                _cameraFps,
                _r1Status,
                _r2Status,
                _r1RpiTemp,
                _r2RpiTemp
            ));
        }

        private RobotBridgeService BuildBridge(string robotId, string ip, bool hasCamera)
        {
            bool isR1 = robotId.EndsWith("01");

            var bridge = new RobotBridgeService
            {
                RobotId = robotId,
                RosIp = ip,
                RosPort = 9090,
                RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot",
                HasCamera = hasCamera
            };

            // Connection state
            bridge.OnInstanceConnectionChanged += connected =>
            {
                _logger.LogInformation("[{RobotId}] Connection: {State}", robotId, connected ? "UP" : "DOWN");
                if (isR1) _r1Connected = connected;
                else _r2Connected = connected;
                PublishSnapshot();
            };

            // Hardware telemetry (RPi temperature, calibration flags)
            bridge.OnHardwareStatusUpdated += hw =>
            {
                if (isR1) _r1RpiTemp = hw.RpiTemp;
                else _r2RpiTemp = hw.RpiTemp;
            };

            // Robot operational status string (STANDBY, LEARNING, CALIBRATING, etc.)
            bridge.OnRobotStatusUpdated += s =>
            {
                if (isR1) _r1Status = s;
                else _r2Status = s;
            };

            return bridge;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[RobotBridgeWorker] Stopping bridges ...");

            // Unsubscribe the shared static log handler before stopping
            RobotBridgeService.OnLog -= _logHandler;

            if (_bridge1 != null) await _bridge1.StopAsync();
            if (_bridge2 != null) await _bridge2.StopAsync();
            await base.StopAsync(cancellationToken);
        }
    }
}
