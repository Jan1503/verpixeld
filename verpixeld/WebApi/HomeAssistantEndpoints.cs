using System.Text.Json;
using CanvasManagement.Interfaces;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     Read-only endpoints for discovering Home Assistant entities (to find exact entity ids when configuring
///     an HA Sensor tile) and checking the connection status. The live data comes from the in-process
///     <see cref="HomeAssistantBridge" />, populated by <c>HomeAssistantService</c>.
/// </summary>
public static class HomeAssistantEndpoints
{
    public static void MapHomeAssistantEndpoints(this WebApplication app)
    {
        var homeAssistant = app.Services.GetRequiredService<HomeAssistantService>();
        app.MapGet("/api/homeassistant/status", () =>
            Results.Json(new ApiResponse<object>(true, new
            {
                connected = HomeAssistantBridge.Connected,
                entityCount = HomeAssistantBridge.All().Count,
                mqtt = homeAssistant?.MqttAvailable ?? false,
                device = homeAssistant?.WallDevice?.Status()
            })));

        app.MapGet("/api/homeassistant/device", () =>
        {
            var d = homeAssistant?.WallDevice?.Status();
            return d == null
                ? ApiResponse.Fail("Wall device is not running")
                : ApiResponse.Ok(d);
        });

        // List known entities. ?q= searches name/id; ?domain=sensor limits to that HA domain.
        app.MapGet("/api/homeassistant/entities", (string? q, string? domain) =>
        {
            var all = HomeAssistantBridge.All();
            var prefix = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim() + ".";
            var items = all
                .Where(e => prefix == null || e.EntityId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrWhiteSpace(q) ||
                            e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            (e.FriendlyName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FriendlyName ?? e.EntityId, StringComparer.OrdinalIgnoreCase)
                .Select(e => new
                {
                    entityId = e.EntityId,
                    friendlyName = e.FriendlyName,
                    state = e.State,
                    unit = e.Unit,
                    domain = DomainOf(e.EntityId)
                })
                .ToArray();
            return Results.Json(new ApiResponse<object>(true, items));
        });

        // Overlay self-test (does not go through Home Assistant).
        app.MapPost("/api/homeassistant/toast", async (HttpContext context) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(await new StreamReader(context.Request.Body).ReadToEndAsync());
                var root = doc.RootElement;
                var title = root.TryGetProperty("title", out var t) ? t.GetString() : "Home Assistant";
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                var severity = root.TryGetProperty("severity", out var s) ? s.GetString() : null;
                var notificationId = root.TryGetProperty("notificationId", out var id) ? id.GetString() : null;
                if (string.IsNullOrWhiteSpace(message))
                    return ApiResponse.Fail("message is required");
                HomeAssistantBridge.RaiseNotification(title ?? "Home Assistant", message, notificationId, severity);
                return ApiResponse.Ok(new { title, message, severity }, "Toast queued");
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });

        // REST notify target (notify.rest / rest_command). Same overlay as MQTT notify.verpixeld_wall.
        app.MapPost("/api/homeassistant/notify", async (HttpContext context) =>
        {
            try
            {
                var raw = await new StreamReader(context.Request.Body).ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(raw))
                    return ApiResponse.Fail("body is required");
                HaWallDevice.ApplyNotify(raw);
                return ApiResponse.Ok(new { queued = true }, "Notification queued");
            }
            catch (Exception ex)
            {
                return ApiResponse.Error(ex);
            }
        });
    }

    private static string DomainOf(string entityId)
    {
        var dot = (entityId ?? "").IndexOf('.');
        return dot > 0 ? entityId![..dot] : "";
    }
}
