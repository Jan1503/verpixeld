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
    }

    private static string DomainOf(string entityId)
    {
        var dot = (entityId ?? "").IndexOf('.');
        return dot > 0 ? entityId![..dot] : "";
    }
}
