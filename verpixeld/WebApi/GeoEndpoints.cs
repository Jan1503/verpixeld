using System.Text.Json;
using System.Text.Json.Serialization;

namespace verpixeld.WebApi;

/// <summary>
///     Place search for extension location pickers (OpenStreetMap Nominatim, no API key).
/// </summary>
public static class GeoEndpoints
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void MapGeoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/geo/search", async (string? q) =>
        {
            var query = (q ?? "").Trim();
            if (query.Length < 2)
                return Results.Json(new ApiResponse<object>(true, Array.Empty<object>()));

            try
            {
                var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=8&addressdetails=0&q="
                          + Uri.EscapeDataString(query);
                var json = await Http.GetStringAsync(url);
                var hits = JsonSerializer.Deserialize<List<NominatimHit>>(json, JsonOpts) ?? [];
                var items = hits
                    .Where(h => !string.IsNullOrWhiteSpace(h.Lat) && !string.IsNullOrWhiteSpace(h.Lon))
                    .Select(h =>
                    {
                        var display = h.DisplayName ?? h.Name ?? query;
                        var shortName = (h.Name ?? display.Split(',')[0]).Trim();
                        return new
                        {
                            name = shortName,
                            displayName = display,
                            lat = h.Lat,
                            lon = h.Lon
                        };
                    })
                    .ToArray();
                return Results.Json(new ApiResponse<object>(true, items));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GEO] search failed: {ex.Message}");
                return Results.Json(new ApiResponse<object>(false, Error: "Place search failed"));
            }
        });
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "verpixeld/1.0 (https://github.com/Jan1503/verpixeld)");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return http;
    }

    private sealed class NominatimHit
    {
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("lat")] public string? Lat { get; set; }
        [JsonPropertyName("lon")] public string? Lon { get; set; }
    }
}
