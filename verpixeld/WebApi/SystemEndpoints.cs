using System.Diagnostics;
using verpixeld.Configuration;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     System-related API endpoints (status, reboot, restart)
/// </summary>
[LogTag("REBOOT")]
public static class SystemEndpoints
{
    /// <summary>Set when the endpoints are mapped; handlers in helper methods log through it too.</summary>
    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public static void MapSystemEndpoints(this WebApplication app)
    {
        Log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SystemEndpoints));
        var ctx = app.Services.GetRequiredService<EndpointContext>();
        var render = app.Services.GetRequiredService<IRenderService>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

        // System status
        app.MapGet("/api/status", () =>
        {
            var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var fps = ctx.GetCurrentFps();

            var resolution = ctx.DisplayResolution;

            var isRunning = ctx.CanvasManager.GetCanvases.Count > 0;

            var status = new SystemStatus(
                ctx.GetLocalIp()?.ToString() ?? "Unknown",
                isRunning,
                ctx.CanvasManager.GetFilterCount(),
                resolution,
                (long)uptime.TotalSeconds,
                $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m",
                fps > 0 ? $"{fps:F1}" : "--",
                AppPaths.RunningInContainer()
            );
            return ApiResponse.Ok(status);
        });

        // Host reboot, or process exit in Docker (compose `restart: unless-stopped` brings it back).
        app.MapPost("/api/system/reboot", () =>
        {
            try
            {
                if (AppPaths.RunningInContainer())
                {
                    Log.LogInformation("Container restart requested — stopping the process");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(800);
                        lifetime.StopApplication();
                    });
                    return ApiResponse.Ok(new
                    {
                        mode = "container",
                        message = "Container is restarting. Docker will bring the process back if restart is unless-stopped."
                    });
                }

                Log.LogInformation("System reboot requested via API");

                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);

                    // Try multiple methods for Linux reboot
                    // Requires polkit rule: /etc/polkit-1/rules.d/10-allow-reboot.rules
                    var methods = new[]
                    {
                        ("systemctl", "reboot"), // systemd + polkit (preferred)
                        ("/sbin/reboot", ""), // direct call (if root)
                        ("sudo", "/sbin/reboot") // with sudo fallback
                    };

                    foreach (var (cmd, args) in methods)
                        try
                        {
                            Console.WriteLine($"[REBOOT] Trying: {cmd} {args}".Trim());
                            var psi = new ProcessStartInfo
                            {
                                FileName = cmd,
                                Arguments = args ?? "",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };

                            using var process = Process.Start(psi);
                            if (process != null)
                            {
                                var error = await process.StandardError.ReadToEndAsync();
                                await process.WaitForExitAsync();

                                if (process.ExitCode == 0)
                                {
                                    Log.LogInformation("Success with: {Cmd} {Args}", cmd, args);
                                    return; // Reboot initiated
                                }

                                Log.LogError("Failed ({ExitCode}): {Error}", process.ExitCode, error);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.LogError("{Cmd} failed: {Message}", cmd, ex.Message);
                        }

                    // Try Windows as last resort
                    try
                    {
                        Log.LogInformation("Trying Windows shutdown...");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "shutdown",
                            Arguments = "/r /t 0",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                    }
                    catch (Exception winEx)
                    {
                        Log.LogError("All methods failed. Last error: {Message}", winEx.Message);
                        Log.LogInformation("Configure sudoers: daemon ALL=(ALL) NOPASSWD: /sbin/reboot");
                    }
                });

                return ApiResponse.Ok(new
                {
                    mode = "host",
                    message = "System reboot initiated. The device will restart shortly.",
                    hint = "If reboot fails, run: sudo visudo and add: daemon ALL=(ALL) NOPASSWD: /sbin/reboot"
                });
            }
            catch (Exception ex)
            {
                Log.LogError("Request failed: {Message}", ex.Message);
                return ApiResponse.Fail($"Failed to initiate reboot: {ex.Message}");
            }
        });

        app.MapPost("/api/system/restart-render", () =>
        {
            try
            {
                Log.LogInformation("[API] Render loop restart requested");
                render.Restart();
                return ApiResponse.Ok("Render loop restarted successfully.");
            }
            catch (Exception ex)
            {
                Log.LogError("[API] Render loop restart failed: {Message}", ex.Message);
                return ApiResponse.Fail($"Failed to restart render loop: {ex.Message}");
            }
        });
    }
}
