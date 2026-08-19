using Microsoft.Extensions.DependencyInjection;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for camera motion alerts.
///     The /api/alert/trigger endpoint is designed to be called by a camera's
///     webhook/HTTP action on motion detection.
/// </summary>
public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this WebApplication app)
    {
        var alertService = app.Services.GetRequiredService<AlertService>();

        var group = app.MapGroup("/api/alert");

        // Trigger the alert (called by camera webhook - no body needed)
        group.MapPost("/trigger", () =>
        {
            try
            {
                alertService.TriggerAlert();
                return Results.Json(new
                {
                    success = true,
                    active = alertService.IsActive,
                    message = alertService.IsActive ? "Alert triggered" : "No stream URL configured"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ALERT] Trigger error: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });

        // Dismiss the alert (from GUI or API)
        group.MapPost("/dismiss", () =>
        {
            try
            {
                alertService.DismissAlert();
                return ApiResponse.Ok("Alert dismissed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ALERT] Dismiss error: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });

        // Get alert status
        group.MapGet("/status", () =>
        {
            return Results.Json(new
            {
                success = true,
                active = alertService.IsActive,
                streamUrl = alertService.StreamUrl,
                timeoutSeconds = alertService.TimeoutSeconds,
                remainingSeconds = alertService.RemainingSeconds,
                scaleFilter = alertService.ScaleFilter
            });
        });

        // Configure alert settings
        group.MapPost("/configure", (string? streamUrl, int? timeoutSeconds, string? scaleFilter) =>
        {
            try
            {
                alertService.Configure(streamUrl, timeoutSeconds, scaleFilter);
                return Results.Json(new
                {
                    success = true,
                    streamUrl = alertService.StreamUrl,
                    timeoutSeconds = alertService.TimeoutSeconds,
                    scaleFilter = alertService.ScaleFilter,
                    message = "Alert configured"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ALERT] Configure error: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });
    }
}
