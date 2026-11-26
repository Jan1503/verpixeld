using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.Layout;

namespace verpixeld.WebApi;

/// <summary>
///     Night mode API endpoints - Automatic brightness scheduling
/// </summary>
[LogTag("API")]
public static class NightModeEndpoints
{
    /// <summary>Set when the endpoints are mapped; handlers in helper methods log through it too.</summary>
    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public static void MapNightModeEndpoints(this WebApplication app)
    {
        Log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NightModeEndpoints));
        var ctx = app.Services.GetRequiredService<EndpointContext>();

        // Get night mode configuration
        app.MapGet("/api/nightmode/config", () =>
        {
            try
            {
                var config = ctx.NightModeManager!.GetConfiguration();
                return ApiResponse.Ok(config);
            }
            catch (Exception ex)
            {
                Log.LogError("Error getting night mode config: {Message}", ex.Message);
                return ApiResponse.Error(ex);
            }
        });

        // Update night mode configuration
        app.MapPost("/api/nightmode/config", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();

                var config = JsonSerializer.Deserialize<NightModeConfiguration>(body);
                if (config == null)
                    return ApiResponse.Fail("Invalid configuration");

                ctx.NightModeManager!.UpdateConfiguration(config);

                Log.LogInformation("Night mode configuration updated. Enabled: {Enabled}", config.Enabled);

                return ApiResponse.Ok(new
                {
                    message = "Night mode configuration updated successfully",
                    config
                });
            }
            catch (Exception ex)
            {
                Log.LogError("Error updating night mode config: {Message}", ex.Message);
                return ApiResponse.Error(ex);
            }
        });

        // Get night mode status
        app.MapGet("/api/nightmode/status", () =>
        {
            try
            {
                var (isActive, currentBrightness, targetBrightness, mode) = ctx.NightModeManager!.GetStatus();

                return ApiResponse.Ok(new
                {
                    isActive,
                    mode,
                    currentBrightness,
                    targetBrightness,
                    currentPercentage = (int)(currentBrightness * 100),
                    targetPercentage = (int)(targetBrightness * 100)
                });
            }
            catch (Exception ex)
            {
                Log.LogError("Error getting night mode status: {Message}", ex.Message);
                return ApiResponse.Error(ex);
            }
        });

        // Force night mode update (immediate brightness check and apply)
        app.MapPost("/api/nightmode/force-update", () =>
        {
            try
            {
                Log.LogInformation("Force night mode update requested");
                ctx.NightModeManager!.ForceUpdateImmediate();

                var (isActive, currentBrightness, targetBrightness, mode) = ctx.NightModeManager.GetStatus();

                Log.LogInformation("Night mode update applied - Mode: {Mode}, Brightness: {CurrentBrightness:F2}", mode, currentBrightness);

                return ApiResponse.Ok(new
                {
                    message = "Night mode updated successfully",
                    mode,
                    brightness = currentBrightness,
                    isActive
                });
            }
            catch (Exception ex)
            {
                Log.LogError("Error forcing night mode update: {Message}", ex.Message);
                return ApiResponse.Error(ex);
            }
        });
    }
}
