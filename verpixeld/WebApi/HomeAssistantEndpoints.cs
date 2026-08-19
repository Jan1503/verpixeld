using CanvasManagement.Interfaces;

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
        app.MapGet("/api/homeassistant/status", () =>
            Results.Json(new ApiResponse<object>(true, new
            {
                connected = HomeAssistantBridge.Connected,
                entityCount = HomeAssistantBridge.All().Count
            })));

        // List known entities, optionally filtered by a substring (?q=energy).
        app.MapGet("/api/homeassistant/entities", (string? q) =>
        {
            var all = HomeAssistantBridge.All();
            var items = all
                .Where(e => string.IsNullOrWhiteSpace(q) ||
                            e.EntityId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            (e.FriendlyName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(e => new
                {
                    entityId = e.EntityId,
                    friendlyName = e.FriendlyName,
                    state = e.State,
                    unit = e.Unit
                })
                .ToArray();
            return Results.Json(new ApiResponse<object>(true, items));
        });
    }
}
