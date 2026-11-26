using verpixeld.Services;
using verpixeld.Interfaces;
using CanvasManagement;
using System.Text.Json;
using verpixeld.Layout;

namespace verpixeld.WebApi;

/// <summary>
///     Layout scheduler API endpoints - Time-based automatic layout switching
/// </summary>
[LogTag("API")]
public static class ScheduleEndpoints
{
    /// <summary>Set when the endpoints are mapped; handlers in helper methods log through it too.</summary>
    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public static void MapScheduleEndpoints(this WebApplication app)
    {
        Log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ScheduleEndpoints));
        var scheduleManager = app.Services.GetRequiredService<ILayoutScheduleManager>();

        // Get all schedules
        app.MapGet("/api/schedule/list", () =>
        {
            try
            {
                var schedules = scheduleManager!.GetAllSchedules();
                return Results.Json(new { success = true, data = schedules });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Get active schedule
        app.MapGet("/api/schedule/active", () =>
        {
            try
            {
                var active = scheduleManager!.GetActiveSchedule();
                if (active == null)
                    return Results.Json(new { success = true, data = (object?)null });

                return Results.Json(new { success = true, data = active });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Get specific schedule
        app.MapGet("/api/schedule/{scheduleName}", (string scheduleName) =>
        {
            try
            {
                var schedule = scheduleManager!.GetSchedule(scheduleName);
                if (schedule == null)
                    return Results.Json(new { success = false, error = $"Schedule '{scheduleName}' not found" });

                return Results.Json(new { success = true, data = schedule });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Save schedule
        app.MapPost("/api/schedule/save", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();

                var schedule = JsonSerializer.Deserialize<LayoutSchedule>(body);
                if (schedule == null)
                    return Results.Json(new { success = false, error = "Invalid schedule data" });

                var saved = scheduleManager!.SaveSchedule(schedule);
                if (!saved)
                    return Results.Json(new { success = false, error = "Failed to save schedule" });

                // Handle activation/deactivation based on enabled state
                if (schedule.Enabled)
                {
                    // Activate the schedule if it's enabled
                    scheduleManager.ActivateSchedule(schedule.Name);
                    Log.LogInformation("Auto-activated enabled schedule '{Name}'", schedule.Name);
                }
                else
                {
                    // Deactivate if this schedule was the active one
                    var activeSchedule = scheduleManager.GetActiveSchedule();
                    if (activeSchedule != null && activeSchedule.Name == schedule.Name)
                    {
                        scheduleManager.ClearActiveSchedule();
                        Log.LogInformation("Deactivated schedule '{Name}' (was disabled)", schedule.Name);

                        // Try to activate another enabled schedule
                        scheduleManager.AutoActivateIfNeeded();
                    }
                }

                return Results.Json(new
                {
                    success = true,
                    data =
                        $"Schedule '{schedule.Name}' saved successfully{(schedule.Enabled ? " and activated" : " and deactivated")}"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Delete schedule
        app.MapDelete("/api/schedule/{scheduleName}", (string scheduleName) =>
        {
            try
            {
                var deleted = scheduleManager!.DeleteSchedule(scheduleName);
                if (!deleted)
                    return Results.Json(new { success = false, error = $"Schedule '{scheduleName}' not found" });

                return Results.Json(new
                {
                    success = true,
                    data = $"Schedule '{scheduleName}' deleted successfully"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Activate schedule
        app.MapPost("/api/schedule/activate/{scheduleName}", (string scheduleName) =>
        {
            try
            {
                var activated = scheduleManager!.ActivateSchedule(scheduleName);
                if (!activated)
                    return Results.Json(new { success = false, error = $"Schedule '{scheduleName}' not found" });

                return Results.Json(new
                {
                    success = true,
                    data = $"Schedule '{scheduleName}' is now active"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Set as default schedule
        app.MapPost("/api/schedule/{scheduleName}/set-default", (string scheduleName) =>
        {
            try
            {
                var success = scheduleManager!.SetDefaultSchedule(scheduleName);
                if (!success)
                    return Results.Json(new { success = false, error = $"Schedule '{scheduleName}' not found" });

                return Results.Json(new
                {
                    success = true,
                    data = $"Schedule '{scheduleName}' set as default"
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Get next scheduled change
        app.MapGet("/api/schedule/next", () =>
        {
            try
            {
                var next = scheduleManager!.GetNextScheduledChange();
                if (next == null)
                    return Results.Json(new { success = true, data = (object?)null });

                var (entry, timeUntil) = next.Value;
                if (entry == null)
                    return Results.Json(new { success = true, data = (object?)null });

                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        layoutName = entry.LayoutName,
                        time = entry.Time,
                        description = entry.Description,
                        timeUntil = new
                        {
                            totalSeconds = (int)timeUntil.TotalSeconds,
                            hours = timeUntil.Hours,
                            minutes = timeUntil.Minutes,
                            formatted = $"{timeUntil.Hours}h {timeUntil.Minutes}m"
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });
    }
}
