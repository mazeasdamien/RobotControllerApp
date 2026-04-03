using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotHub.Services;
using System.Text.Json;

namespace RobotHub.Workers
{
    // Handles UnityPushServer lifecycle:
    //   - Broadcasts RealSense intrinsics to Unity on each new client connection.
    //   - Disposes all WebSocket clients cleanly on shutdown.
    // Route registration is done in Program.cs via pushServer.MapRoutes(app).
    public sealed class UnityPushWorker : BackgroundService
    {
        private readonly UnityPushServer _server;
        private readonly ILogger<UnityPushWorker> _logger;

        public UnityPushWorker(UnityPushServer server, ILogger<UnityPushWorker> logger)
        {
            _server = server;
            _logger = logger;

            // Wire RealSense intrinsics broadcast — fires when Unity connects.
            // GetColorIntrinsics() is cached after the first call.
            _server.OnClientConnected += async () =>
            {
                var intr = RealSenseIntrinsics.GetColorIntrinsics();
                if (intr.HasValue)
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        width  = intr.Value.width,
                        height = intr.Value.height,
                        fx     = intr.Value.fx,
                        fy     = intr.Value.fy,
                        cx     = intr.Value.ppx,
                        cy     = intr.Value.ppy
                    });
                    await _server.BroadcastAsync("setRealSenseIntrinsics", payload);
                    _logger.LogInformation(
                        "[UnityPushWorker] RealSense intrinsics sent: {w}x{h} fx={fx:F1} fy={fy:F1}",
                        intr.Value.width, intr.Value.height, intr.Value.fx, intr.Value.fy);
                }
                else
                {
                    _logger.LogWarning("[UnityPushWorker] RealSense not available — intrinsics not sent.");
                }
            };
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Routes are already registered by MapRoutes() in Program.cs before app.Run().
            // This worker only needs to keep running until the host stops.
            _logger.LogInformation("[UnityPushWorker] Running — waiting for Unity connections.");
            return Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => Task.CompletedTask);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[UnityPushWorker] Disposing active WebSocket clients ...");
            await _server.StopAsync();
            await base.StopAsync(cancellationToken);
        }
    }
}
