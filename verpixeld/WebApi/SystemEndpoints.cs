using System.Diagnostics;

namespace verpixeld.WebApi;

/// <summary>
///     System-related API endpoints (status, reboot, restart)
/// </summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app, EndpointContext ctx)
    {
        // System status
        app.MapGet("/api/status", () =>
        {
            var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var fps = ctx.GetCurrentFps();

            // Get display resolution from context (set during initialization)
            var resolution = ctx.DisplayResolution ?? "384x192";

            // Check if canvas manager has canvases (indicates it's running)
            var isRunning = ctx.CanvasManager.GetCanvases.Count > 0;

            var status = new SystemStatus(
                ctx.GetLocalIp()?.ToString() ?? "Unknown",
                isRunning,
                ctx.CanvasManager.GetFilterCount(),
                resolution,
                (long)uptime.TotalSeconds,
                $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m",
                fps > 0 ? $"{fps:F1}" : "--"
            );
            return ApiResponse.Ok(status);
        });

        // System reboot endpoint
        // NOTE: Requires sudoers configuration for passwordless reboot:
        //   sudo visudo
        //   Add: daemon ALL=(ALL) NOPASSWD: /sbin/reboot, /sbin/shutdown
        app.MapPost("/api/system/reboot", async () =>
        {
            try
            {
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

        // Restart render loop endpoint
        app.MapPost("/api/system/restart-render", () =>
        {
            try
            {
                Console.WriteLine("[API] Render loop restart requested");
                Program.RestartRenderLoop();
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
