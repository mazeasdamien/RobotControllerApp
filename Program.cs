using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RobotHub;
using RobotHub.Services;
using RobotHub.Workers;
using System.Text;
using System.Text.Json;

// ─────────────────────────────────────────────────────────────────────────────
// Robot Orange Hub — headless ASP.NET Core 8 Worker Service
//
// Single port (5000) — one Cloudflare tunnel, one Kestrel instance:
//
//   ws://  :5000/robot?robotId=X   — robot bridge endpoint
//   ws://  :5000/unity?robotId=X   — expert Unity endpoint
//   ws://  :5000/scene3d-ws        — 3D scene broadcast (pose + joints)
//   http:// :5000/image            — latest robot camera frame (JPEG)
//   http:// :5000/joints?robotId=X — joint state polling
//   http:// :5000/image_operator   — latest operator webcam frame
//   http:// :5000/library/{file}   — GLB asset streaming
//   http:// :5000/status           — JSON health check
//   http:// :5000/status/sse       — Server-Sent Events telemetry stream
//   http:// :5000/ui               — browser monitoring dashboard
//
// Cloudflare tunnel config:
//   ingress:
//     - hostname: niryo.dmzs-lab.com
//       service: http://localhost:5000
//     - service: http_status:404
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// ── Windows Service support (SCM auto-restart) ────────────────────────────────
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "RobotOrangeHub";
});

// ── Single Kestrel port — Cloudflare tunnel terminates TLS externally ─────────
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(5000);
});

// TCP_NODELAY — flush small WebSocket frames (ping/pong, pose) immediately
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions>(opts =>
{
    opts.NoDelay = true;
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// ── Core services ─────────────────────────────────────────────────────────────
var settings = AppSettings.Load();
builder.Services.AddSingleton(settings);

// ConnectionManager is a singleton shared by RelayServerHost and workers
builder.Services.AddSingleton<ConnectionManager>();

// UnityPushServer is a singleton so UnityPushWorker can call StopAsync() on shutdown
builder.Services.AddSingleton<UnityPushServer>(sp =>
    new UnityPushServer { LibraryPath = settings.LibraryPath });

// ── Background workers ────────────────────────────────────────────────────────
// RelayServerWorker removed — relay routes are registered directly below.
// UnityPushWorker handles RealSense intrinsics broadcast + client cleanup on stop.
builder.Services.AddHostedService<UnityPushWorker>();
builder.Services.AddHostedService<RobotBridgeWorker>();

var app = builder.Build();

app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(60) });

// Serve wwwroot/dashboard.html for the browser monitoring UI
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(AppContext.BaseDirectory, "wwwroot")),
    RequestPath = ""
});

// ── Register relay routes (robot bridge + Unity expert) ───────────────────────
var manager = app.Services.GetRequiredService<ConnectionManager>();

// Subscribe relay log events to the .NET logging pipeline
RobotRelayService.OnLog += msg =>
    app.Services.GetRequiredService<ILoggerFactory>()
       .CreateLogger("RelayServerHost")
       .LogInformation("{Message}", msg);

RobotRelayService.MapRoutes(app, manager);

// ── Register Scene3D routes (WebSocket + GLB library) ────────────────────────
var scene3d = app.Services.GetRequiredService<UnityPushServer>();
scene3d.MapRoutes(app);

// ── Management endpoints ─────────────────────────────────────────────────────

// GET /ui — redirect to the glassmorphism dashboard
app.MapGet("/ui", context =>
{
    context.Response.Redirect("/dashboard.html");
    return Task.CompletedTask;
});

// GET /status — JSON health check
app.MapGet("/status", () => Results.Ok(new
{
    service   = "RobotOrangeHub",
    ok        = true,
    port      = 5000,
    r1Ip      = settings.RobotIp,
    r2Ip      = settings.Robot2Ip,
    timestamp = DateTime.UtcNow
}));

// GET /status/sse — Server-Sent Events telemetry stream (consumed by dashboard.html)
app.MapGet("/status/sse", async (HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers["Content-Type"]      = "text/event-stream";
    ctx.Response.Headers["Cache-Control"]     = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    await ctx.Response.Body.FlushAsync(ct);

    await foreach (var telemetry in TelemetryBus.Reader.ReadAllAsync(ct))
    {
        var json  = JsonSerializer.Serialize(telemetry);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await ctx.Response.Body.WriteAsync(bytes, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
});

// POST /api/learning — Toggles the physical joints learning mode for all robots
app.MapPost("/api/learning/{state}", async (bool state, ConnectionManager mgr) =>
{
    var cmd = JsonSerializer.Serialize(new 
    { 
        op = "call_service", 
        service = "/niryo_robot/learning_mode/activate", 
        type = "niryo_robot_msgs/SetBool", 
        args = new { value = state } 
    });

    // Send to both robots
    await mgr.SendToRobotClient("Robot_Niryo_01", cmd);
    await mgr.SendToRobotClient("Robot_Niryo_02", cmd);

    return Results.Ok(new { success = true, learning_mode = state });
});

// POST /api/calibrate/{robotId} — Triggers hardware auto-calibration sequence
app.MapPost("/api/calibrate/{robotId}", async (string robotId, ConnectionManager mgr) =>
{
    var requestCalib = JsonSerializer.Serialize(new 
    { 
        op = "call_service", 
        service = "/niryo_robot/joints_interface/request_new_calibration", 
        type = "niryo_robot_msgs/SetInt", 
        args = new { value = 1 } 
    });

    var startCalib = JsonSerializer.Serialize(new 
    { 
        op = "call_service", 
        service = "/niryo_robot/joints_interface/calibrate_motors", 
        type = "niryo_robot_msgs/SetInt", 
        args = new { value = 1 } 
    });

    await mgr.SendToRobotClient(robotId, requestCalib);
    await Task.Delay(1500); // Hardware requires wait window before starting
    await mgr.SendToRobotClient(robotId, startCalib);

    return Results.Ok(new { success = true });
});

app.Run();
