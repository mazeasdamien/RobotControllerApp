using System.Threading.Channels;

namespace RobotHub
{
    // Snapshot of all live metrics pushed by services, consumed by the SSE endpoint.
    public sealed record TelemetryEvent(
        bool R1Connected,
        bool R2Connected,
        bool UnityConnected,
        float[] R1Joints,
        float[] R2Joints,
        long UnityLatencyMs,
        int CameraFps,
        string R1Status,
        string R2Status,
        int R1RpiTemp,
        int R2RpiTemp
    );

    // Singleton in-process telemetry channel. Services write, SSE endpoint reads.
    // UnboundedChannel is safe here: the SSE reader always keeps up — it is just
    // forwarding JSON strings over HTTP. Backpressure is handled by the SSE
    // writer dropping the oldest entry when the channel grows beyond MaxCapacity.
    public static class TelemetryBus
    {
        private static readonly Channel<TelemetryEvent> _channel =
            Channel.CreateBounded<TelemetryEvent>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = false,
                SingleReader = false
            });

        public static ChannelWriter<TelemetryEvent> Writer => _channel.Writer;
        public static ChannelReader<TelemetryEvent> Reader => _channel.Reader;
    }
}
