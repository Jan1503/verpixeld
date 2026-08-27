using System.Diagnostics;
using verpixeld.Configuration;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     System-related API endpoints (status, reboot, restart)
/// </summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
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
                    Console.WriteLine("[REBOOT] Container restart requested — stopping the process");
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

                Console.WriteLine("[REBOOT] System reboot requested via API");

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
                                    Console.WriteLine($"[REBOOT] Success with: {cmd} {args}");
                                    return; // Reboot initiated
                                }

                                Console.WriteLine($"[REBOOT] Failed ({process.ExitCode}): {error}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[REBOOT] {cmd} failed: {ex.Message}");
                        }

                    // Try Windows as last resort
                    try
                    {
                        Console.WriteLine("[REBOOT] Trying Windows shutdown...");
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
                        Console.WriteLine($"[REBOOT] All methods failed. Last error: {winEx.Message}");
                        Console.WriteLine("[REBOOT] Configure sudoers: daemon ALL=(ALL) NOPASSWD: /sbin/reboot");
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
                Console.WriteLine($"[REBOOT] Request failed: {ex.Message}");
                return ApiResponse.Fail($"Failed to initiate reboot: {ex.Message}");
            }
        });

        app.MapPost("/api/system/restart-render", () =>
        {
            try
            {
                Console.WriteLine("[API] Render loop restart requested");
                render.Restart();
                return ApiResponse.Ok("Render loop restarted successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Render loop restart failed: {ex.Message}");
                return ApiResponse.Fail($"Failed to restart render loop: {ex.Message}");
            }
        });
    }
}
