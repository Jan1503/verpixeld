using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.Layout;

namespace verpixeld.WebApi;

/// <summary>
///     Layout scheduler API endpoints - Time-based automatic layout switching
/// </summary>
public static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this WebApplication app)
    {
        var ctx = app.Services.GetRequiredService<EndpointContext>();

        // Get all schedules
        app.MapGet("/api/schedule/list", () =>
        {
            try
            {
                var schedules = ctx.ScheduleManager!.GetAllSchedules();
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
                var active = ctx.ScheduleManager!.GetActiveSchedule();
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
                var schedule = ctx.ScheduleManager!.GetSchedule(scheduleName);
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

                var saved = ctx.ScheduleManager!.SaveSchedule(schedule);
                if (!saved)
                    return Results.Json(new { success = false, error = "Failed to save schedule" });

                // Handle activation/deactivation based on enabled state
                if (schedule.Enabled)
                {
                    // Activate the schedule if it's enabled
                    ctx.ScheduleManager.ActivateSchedule(schedule.Name);
                    Console.WriteLine($"[API] Auto-activated enabled schedule '{schedule.Name}'");
                }
                else
                {
                    // Deactivate if this schedule was the active one
                    var activeSchedule = ctx.ScheduleManager.GetActiveSchedule();
                    if (activeSchedule != null && activeSchedule.Name == schedule.Name)
                    {
                        ctx.ScheduleManager.ClearActiveSchedule();
                        Console.WriteLine($"[API] Deactivated schedule '{schedule.Name}' (was disabled)");

                        // Try to activate another enabled schedule
                        ctx.ScheduleManager.AutoActivateIfNeeded();
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
                var deleted = ctx.ScheduleManager!.DeleteSchedule(scheduleName);
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
                var activated = ctx.ScheduleManager!.ActivateSchedule(scheduleName);
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
                var success = ctx.ScheduleManager!.SetDefaultSchedule(scheduleName);
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
                var next = ctx.ScheduleManager!.GetNextScheduledChange();
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
